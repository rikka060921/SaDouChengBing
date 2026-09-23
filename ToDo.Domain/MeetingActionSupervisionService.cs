using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public sealed record MeetingActionSupervisionRunResult(
    int Evaluated,
    int StateChanges,
    int EventsCreated,
    int NotificationsCreated);

/// <summary>
/// 以正式任务为事实源计算会议行动项状态。只在状态变化、升级或跨日提醒时产生事件和通知。
/// </summary>
public sealed class MeetingActionSupervisionService
{
    private readonly ApplicationDbContext _context;
    private readonly DistributedLeaseService _leases;

    public MeetingActionSupervisionService(ApplicationDbContext context)
        : this(context, new DistributedLeaseService(context)) { }

    public MeetingActionSupervisionService(
        ApplicationDbContext context,
        DistributedLeaseService leases)
    {
        _context = context;
        _leases = leases;
    }

    public async Task<MeetingActionSupervisionRunResult> RunAsync(
        DateTime now,
        int? meetingMinutesId = null,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _leases.TryAcquireAsync(
            "task-attention:all",
            TimeSpan.FromMinutes(10),
            cancellationToken);
        if (lease == null) return new MeetingActionSupervisionRunResult(0, 0, 0, 0);

        var query = _context.MeetingActionItems
            .Include(item => item.MeetingMinutes)!
                .ThenInclude(meeting => meeting!.Project)
            .Include(item => item.MatchedTask)
            .Where(item => item.IsConfirmed
                && item.MeetingMinutes != null
                && !item.MeetingMinutes.IsDeleted
                && !item.MeetingMinutes.Project.IsDeleted && item.MeetingMinutes.Project.Status == ProjectStatus.Active
                && (!item.ProjectId.HasValue || _context.Project.Any(p => p.Id == item.ProjectId && !p.IsDeleted && p.Status == ProjectStatus.Active))
                && !_context.MeetingMinutesProjects.Any(link => link.MeetingMinutesId == item.MeetingMinutesId
                    && _context.Project.Any(p => p.Id == link.ProjectId && (p.IsDeleted || p.Status == ProjectStatus.Archived)))
                && item.MeetingMinutes.ConfirmedAt.HasValue);
        if (meetingMinutesId.HasValue)
            query = query.Where(item => item.MeetingMinutesId == meetingMinutesId.Value);

        var items = await query.OrderBy(item => item.Id).ToListAsync(cancellationToken);
        var stateChanges = 0;
        var eventsCreated = 0;
        var notificationsCreated = 0;

        foreach (var item in items)
        {
            var meeting = item.MeetingMinutes!;
            var task = item.MatchedTask is { IsDeleted: false } && item.MatchedTask.ProjectId == meeting.ProjectId
                ? item.MatchedTask : null;
            var state = EvaluateAttention(item, task, now);
            var previousStatus = item.SupervisionStatus;
            var previousLevel = item.EscalationLevel;
            var firstEvaluation = !item.LastSupervisedAt.HasValue;
            var statusChanged = firstEvaluation || previousStatus != state.Status;
            var levelChanged = !statusChanged && previousLevel != state.EscalationLevel;

            if (statusChanged || levelChanged)
            {
                item.SupervisionAcknowledgedByUserId = null;
                item.SupervisionAcknowledgedAt = null;
                item.SnoozedUntil = null;
                item.NextCheckpointAt = null;
                item.SupervisionResolutionNote = string.Empty;
            }

            item.SupervisionStatus = state.Status;
            item.SupervisionMessage = state.Message;
            item.EscalationLevel = state.EscalationLevel;
            item.LastSupervisedAt = now;
            if (statusChanged) stateChanges++;

            var requiresAttention = NeedsAttention(state.Status)
                && task?.AgentExecutionStatus != AgentTaskExecutionStatus.WaitingApproval
                && !(task?.AssigneeType == TaskAssigneeType.DigitalEmployee && state.Status == MeetingActionSupervisionStatus.DueSoon);
            var shouldRemind = requiresAttention
                && !statusChanged
                && !levelChanged
                && (!item.SnoozedUntil.HasValue || item.SnoozedUntil <= now)
                && (!item.NextCheckpointAt.HasValue || item.NextCheckpointAt <= now)
                && (!item.LastReminderAt.HasValue || item.LastReminderAt.Value.Date < now.Date);
            if (!statusChanged && !levelChanged && !shouldRemind) continue;

            var eventType = statusChanged
                ? MeetingActionSupervisionEventType.StateChanged
                : levelChanged
                    ? MeetingActionSupervisionEventType.Escalation
                    : MeetingActionSupervisionEventType.Reminder;
            var eventKey = BuildEventKey(item, task, state, eventType, now);
            if (await _context.MeetingActionSupervisionEvents.AsNoTracking()
                .AnyAsync(evt => evt.EventKey == eventKey, cancellationToken))
            {
                if (requiresAttention && !item.LastReminderAt.HasValue)
                    item.LastReminderAt = now;
                continue;
            }

            var recipientIds = requiresAttention
                ? await ResolveRecipientIdsAsync(item, task, state, cancellationToken)
                : [];
            _context.MeetingActionSupervisionEvents.Add(new MeetingActionSupervisionEvent
            {
                ActionItemId = item.Id,
                MeetingMinutesId = item.MeetingMinutesId,
                ProjectId = meeting.ProjectId,
                TaskId = task?.Id,
                EventType = eventType,
                Status = state.Status,
                EscalationLevel = state.EscalationLevel,
                EventKey = eventKey,
                RecipientIdsJson = JsonSerializer.Serialize(recipientIds),
                Message = state.Message,
                CreatedAt = now
            });
            eventsCreated++;

            if (!requiresAttention || recipientIds.Count == 0) continue;
            item.LastReminderAt = now;
            item.ReminderCount++;
            var (title, type) = BuildNotificationPresentation(state.Status, state.EscalationLevel);
            if (task != null)
            {
                notificationsCreated += await new UserNotificationService(_context).StageTaskAttentionAsync(
                    task, state.Status.ToString(), title, state.Message, now, state.EscalationLevel >= 2, cancellationToken);
            }
            else
            {
                var link = $"/MeetingMinutes/Details/{item.MeetingMinutesId}";
                var existing = await _context.UserNotifications.AsNoTracking().Where(n => n.Link == link && n.Title == title
                    && n.CreatedAt >= now.Date && n.CreatedAt < now.Date.AddDays(1)).Select(n => n.UserId).ToListAsync(cancellationToken);
                existing.AddRange(_context.ChangeTracker.Entries<UserNotification>()
                    .Where(e => e.State == EntityState.Added && e.Entity.Link == link && e.Entity.Title == title)
                    .Select(e => e.Entity.UserId));
                var recipients = recipientIds.Except(existing).ToList();
                _context.UserNotifications.AddRange(recipients.Select(userId => new UserNotification
                {
                    UserId = userId, Title = title, Content = "会议中有尚未关联正式任务的承诺，请补齐关联。",
                    Type = type, Link = link, CreatedAt = now
                }));
                notificationsCreated += recipients.Count;
            }
        }

        if (_context.ChangeTracker.HasChanges())
            await _context.SaveChangesAsync(cancellationToken);

        return new MeetingActionSupervisionRunResult(
            items.Count,
            stateChanges,
            eventsCreated,
            notificationsCreated);
    }

    public static SupervisionState EvaluateAttention(MeetingActionItem item, ToDoTask? task, DateTime now)
    {
        var actionTitle = string.IsNullOrWhiteSpace(item.Title) ? item.Content : item.Title;
        if (item.MatchedTaskId.HasValue && task == null)
            return new(MeetingActionSupervisionStatus.Cancelled, 0, "关联任务已删除或不再可用，不再催办。");
        if (task == null)
            return new(
                MeetingActionSupervisionStatus.PendingLink,
                2,
                $"会议行动项「{actionTitle}」已确认，但没有关联有效的正式任务，请项目负责人补齐关联后再执行。");

        if (task.Status == ToDo.Entities.TaskStatus.Completed || task.IsCompleted)
            return new(
                MeetingActionSupervisionStatus.Completed,
                0,
                $"会议行动项「{actionTitle}」关联任务 #{task.Id} 已完成。");

        if (task.Status == ToDo.Entities.TaskStatus.Cancelled)
            return new(
                MeetingActionSupervisionStatus.Cancelled,
                0,
                $"会议行动项「{actionTitle}」关联任务 #{task.Id} 已取消，请确认会议决策是否仍然有效。");

        var deadline = task.EndTime ?? item.Deadline;
        var hasOwner = task.AssigneeType == TaskAssigneeType.DigitalEmployee
            ? task.AgentDefinitionId.HasValue
            : task.AssigneeId.HasValue;
        if (!hasOwner || !deadline.HasValue)
        {
            var missing = !hasOwner && !deadline.HasValue ? "负责人和截止时间" : !hasOwner ? "负责人" : "截止时间";
            return new(
                MeetingActionSupervisionStatus.NeedsDefinition,
                2,
                $"会议行动项「{actionTitle}」关联任务 #{task.Id} 缺少{missing}，当前无法形成可执行承诺。");
        }

        if (IsBlocked(task))
            return new(
                MeetingActionSupervisionStatus.Blocked,
                task.AgentExecutionStatus == AgentTaskExecutionStatus.Failed || task.ReworkCount >= 3 ? 3 : 2,
                $"会议行动项「{actionTitle}」关联任务 #{task.Id} 当前被阻塞（{BuildBlockReason(task)}），需要人工解除阻塞并给出下一检查点。");

        if (deadline.Value < now)
        {
            var overdue = now - deadline.Value;
            var level = overdue.TotalDays >= 3 ? 3 : overdue.TotalDays >= 1 ? 2 : 1;
            return new(
                MeetingActionSupervisionStatus.Overdue,
                level,
                $"会议行动项「{actionTitle}」关联任务 #{task.Id} 已逾期 {FormatDuration(overdue)}（截止 {deadline:yyyy-MM-dd HH:mm}），请更新进展、阻塞原因和新的承诺时间。");
        }

        var remaining = deadline.Value - now;
        if (remaining <= TimeSpan.FromHours(24))
            return new(
                MeetingActionSupervisionStatus.DueSoon,
                1,
                $"会议行动项「{actionTitle}」关联任务 #{task.Id} 将在 {FormatDuration(remaining)}后到期（{deadline:yyyy-MM-dd HH:mm}），请确认能否按时交付。");

        return new(
            MeetingActionSupervisionStatus.OnTrack,
            0,
            $"会议行动项「{actionTitle}」关联任务 #{task.Id} 正常推进，截止 {deadline:yyyy-MM-dd HH:mm}。");
    }

    private async Task<List<int>> ResolveRecipientIdsAsync(
        MeetingActionItem item,
        ToDoTask? task,
        SupervisionState state,
        CancellationToken cancellationToken)
    {
        var recipients = new AttentionRecipientService(_context);
        if (task != null && state.Status != MeetingActionSupervisionStatus.NeedsDefinition)
            return await recipients.TaskAsync(task, state.EscalationLevel >= 2, cancellationToken);
        return (await recipients.ManagersAsync(item.MeetingMinutes!.ProjectId, cancellationToken)).Take(1).ToList();
    }

    private static bool IsBlocked(ToDoTask task)
        => task.ReworkCount >= 2
            || task.AgentExecutionStatus is AgentTaskExecutionStatus.Failed
                or AgentTaskExecutionStatus.Paused
                or AgentTaskExecutionStatus.WaitingApproval
                or AgentTaskExecutionStatus.AwaitingDispatchConfirmation;

    private static string BuildBlockReason(ToDoTask task)
    {
        if (task.ReworkCount >= 2) return $"已返工 {task.ReworkCount} 次";
        return task.AgentExecutionStatus.GetDisplayName();
    }

    public static bool NeedsAttention(MeetingActionSupervisionStatus status)
        => status is MeetingActionSupervisionStatus.PendingLink
            or MeetingActionSupervisionStatus.NeedsDefinition
            or MeetingActionSupervisionStatus.DueSoon
            or MeetingActionSupervisionStatus.Overdue
            or MeetingActionSupervisionStatus.Blocked;

    private static string BuildEventKey(
        MeetingActionItem item,
        ToDoTask? task,
        SupervisionState state,
        MeetingActionSupervisionEventType eventType,
        DateTime now)
    {
        if (eventType == MeetingActionSupervisionEventType.Reminder)
            return $"meeting-action:{item.Id}:reminder:{state.Status}:{state.EscalationLevel}:{now:yyyyMMdd}";
        if (eventType == MeetingActionSupervisionEventType.Escalation)
            return $"meeting-action:{item.Id}:escalation:{state.Status}:{state.EscalationLevel}:{now:yyyyMMdd}";
        var version = task?.ConcurrencyVersion ?? 0;
        var deadlineTicks = (task?.EndTime ?? item.Deadline)?.Ticks ?? 0;
        return $"meeting-action:{item.Id}:state:{state.Status}:{state.EscalationLevel}:{version}:{deadlineTicks}";
    }

    private static (string Title, string Type) BuildNotificationPresentation(
        MeetingActionSupervisionStatus status,
        int escalationLevel)
    {
        var title = status switch
        {
            MeetingActionSupervisionStatus.PendingLink => "会议行动项未关联任务",
            MeetingActionSupervisionStatus.NeedsDefinition => "会议行动项缺少执行条件",
            MeetingActionSupervisionStatus.DueSoon => "会议行动项即将到期",
            MeetingActionSupervisionStatus.Overdue => "会议行动项已逾期",
            MeetingActionSupervisionStatus.Blocked => "会议行动项执行受阻",
            _ => "会议行动项状态变化"
        };
        return (title, escalationLevel >= 2 ? "Urgent" : "Reminder");
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var safe = duration < TimeSpan.Zero ? duration.Negate() : duration;
        if (safe.TotalDays >= 1) return $"{Math.Max(1, (int)Math.Floor(safe.TotalDays))} 天";
        if (safe.TotalHours >= 1) return $"{Math.Max(1, (int)Math.Floor(safe.TotalHours))} 小时";
        return $"{Math.Max(1, (int)Math.Ceiling(safe.TotalMinutes))} 分钟";
    }

    public sealed record SupervisionState(
        MeetingActionSupervisionStatus Status,
        int EscalationLevel,
        string Message);
}

public sealed class MeetingActionSupervisionHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeetingActionSupervisionHostedService> _logger;

    public MeetingActionSupervisionHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<MeetingActionSupervisionHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MeetingActionSupervisionService>()
                    .RunAsync(AppTime.Now, cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "会议行动项自动督办失败");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

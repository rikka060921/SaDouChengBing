using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using ToDo.Context;
using ToDo.Domain.Options;
using ToDo.Entities;
using TaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Domain;

/// <summary>
/// 持久化 Agent 工作队列。所有自动执行均先入库，再由后台服务领取，避免请求中断导致任务丢失。
/// </summary>
public class AgentWorkQueueService
{
    public static readonly TimeSpan StaleWorkItemTimeout = TimeSpan.FromMinutes(15);

    private static readonly AgentWorkItemStatus[] ActiveStatuses =
    [
        AgentWorkItemStatus.Pending,
        AgentWorkItemStatus.Running,
        AgentWorkItemStatus.WaitingApproval,
        AgentWorkItemStatus.Retrying,
        AgentWorkItemStatus.Paused,
        AgentWorkItemStatus.WaitingPlanConfirmation
    ];

    private static readonly AgentWorkItemStatus[] InFlightStatuses =
    [
        AgentWorkItemStatus.Running,
        AgentWorkItemStatus.WaitingApproval,
        AgentWorkItemStatus.Paused,
        AgentWorkItemStatus.WaitingPlanConfirmation
    ];

    private readonly ApplicationDbContext _context;
    private readonly AgentExecutionService _execution;
    private readonly AiSessionService _sessions;
    private readonly UserNotificationService _notifications;
    private readonly IEventBus _eventBus;
    private readonly AgentOutcomeService _outcomes;
    private readonly ILogger<AgentWorkQueueService> _logger;
    private readonly AgentQueueSignal? _queueSignal;
    private readonly IAgentRegistry? _registry;
    private readonly AgentPlanningService _planning;
    private readonly AgentTelemetry? _telemetry;
    private readonly AgentAutomaticAcceptanceService? _automaticAcceptance;

    public AgentWorkQueueService(
        ApplicationDbContext context,
        AgentExecutionService execution,
        AiSessionService sessions,
        UserNotificationService notifications,
        IEventBus eventBus,
        AgentOutcomeService outcomes,
        ILogger<AgentWorkQueueService> logger,
        AgentQueueSignal? queueSignal = null,
        IAgentRegistry? registry = null,
        AgentTelemetry? telemetry = null,
        AgentAutomaticAcceptanceService? automaticAcceptance = null)
    {
        _context = context;
        _execution = execution;
        _sessions = sessions;
        _notifications = notifications;
        _eventBus = eventBus;
        _outcomes = outcomes;
        _logger = logger;
        _queueSignal = queueSignal;
        _registry = registry;
        _planning = new AgentPlanningService(context, execution, queueSignal);
        _telemetry = telemetry;
        _automaticAcceptance = automaticAcceptance;
    }

    public async Task<AgentWorkItem?> EnqueueTaskAsync(
        int taskId,
        int requestedByUserId,
        AgentWorkTriggerType triggerType,
        int? triggerEntityId,
        string prompt,
        string idempotencyKey,
        string? causeChainId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Agent 工作项幂等键不能为空", nameof(idempotencyKey));

        var existing = await _context.AgentWorkItems
            .FirstOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing != null) return existing;

        var task = await _context.ToDoTasks
            .Include(item => item.AgentDefinition)
            .FirstOrDefaultAsync(item => item.Id == taskId && !item.IsDeleted, cancellationToken);
        if (task == null
            || task.AssigneeType != TaskAssigneeType.DigitalEmployee
            || !task.AgentDefinitionId.HasValue
            || task.Status is TaskStatus.Completed or TaskStatus.Cancelled)
            return null;

        if (task.AgentDefinition == null || !task.AgentDefinition.IsEnabled)
        {
            await MarkEnqueueFailureAsync(task, "任务绑定的 Agent 不存在或已停用，无法进入执行队列", cancellationToken);
            return null;
        }

        var requesterExists = await _context.Users.AsNoTracking()
            .AnyAsync(item => item.Id == requestedByUserId, cancellationToken);
        if (!requesterExists)
        {
            await MarkEnqueueFailureAsync(task, "Agent 工作项发起人不存在，无法进入执行队列", cancellationToken);
            return null;
        }

        var now = AppTime.Now;
        var workItem = new AgentWorkItem
        {
            AgentDefinitionId = task.AgentDefinitionId.Value,
            ProjectId = task.ProjectId,
            TaskId = task.Id,
            RequestedByUserId = requestedByUserId,
            TriggerType = triggerType,
            TriggerEntityId = triggerEntityId,
            IdempotencyKey = idempotencyKey.Trim().Length > 200 ? idempotencyKey.Trim()[..200] : idempotencyKey.Trim(),
            CauseChainId = string.IsNullOrWhiteSpace(causeChainId) ? Guid.NewGuid().ToString("N") : causeChainId.Trim()[..Math.Min(causeChainId.Trim().Length, 32)],
            RequiresPlan = true,
            Prompt = string.IsNullOrWhiteSpace(prompt) ? BuildAssignmentPrompt(task) : prompt.Trim(),
            Status = AgentWorkItemStatus.Pending,
            NextRunAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        _context.AgentWorkItems.Add(workItem);
        task.AgentExecutionStatus = await _context.AgentWorkItems.AsNoTracking()
            .AnyAsync(item => item.TaskId == taskId && item.Status == AgentWorkItemStatus.WaitingPlanConfirmation, cancellationToken)
                ? AgentTaskExecutionStatus.AwaitingPlanConfirmation : AgentTaskExecutionStatus.Pending;
        task.AgentLastError = string.Empty;
        task.UpdatedAt = now;

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            _queueSignal?.Notify();
            _telemetry?.RecordQueued("assignment");
            return workItem;
        }
        catch (DbUpdateException)
        {
            _context.Entry(workItem).State = EntityState.Detached;
            await _context.Entry(task).ReloadAsync(cancellationToken);
            var duplicate = await _context.AgentWorkItems
                .FirstOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
            if (duplicate == null) throw;
            return duplicate;
        }
    }

    public async Task CancelPendingForTaskAsync(
        int taskId,
        string? keepIdempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var now = AppTime.Now;
        var query = _context.AgentWorkItems
            .Where(item => item.TaskId == taskId && ActiveStatuses.Contains(item.Status));
        if (!string.IsNullOrWhiteSpace(keepIdempotencyKey))
            query = query.Where(item => item.IdempotencyKey != keepIdempotencyKey);
        await query
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AgentWorkItemStatus.Cancelled)
                .SetProperty(item => item.CompletedAt, now)
                .SetProperty(item => item.LockedAt, (DateTime?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    public async Task PauseAsync(int workItemId, int operatedByUserId, CancellationToken cancellationToken = default)
    {
        await EnsureSystemAdminAsync(operatedByUserId, cancellationToken);
        var now = AppTime.Now;
        var affected = await _context.AgentWorkItems
            .Where(item => item.Id == workItemId
                && (item.Status == AgentWorkItemStatus.Pending || item.Status == AgentWorkItemStatus.Retrying))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AgentWorkItemStatus.Paused)
                .SetProperty(item => item.LockedAt, (DateTime?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (affected != 1) throw new InvalidOperationException("只有待执行或等待重试的工作项可以暂停");

        var snapshot = await _context.AgentWorkItems.AsNoTracking()
            .FirstAsync(item => item.Id == workItemId, cancellationToken);
        var hasOtherActive = await _context.AgentWorkItems.AsNoTracking()
            .AnyAsync(item => item.TaskId == snapshot.TaskId && item.Id != workItemId && ActiveStatuses.Contains(item.Status), cancellationToken);
        if (!hasOtherActive)
        {
            await _context.ToDoTasks
                .Where(task => task.Id == snapshot.TaskId && task.AgentDefinitionId == snapshot.AgentDefinitionId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(task => task.AgentExecutionStatus, AgentTaskExecutionStatus.Paused)
                    .SetProperty(task => task.UpdatedAt, now), cancellationToken);
        }

        await _eventBus.PublishAsync("agent.work-item.paused", new { workItemId, operatedByUserId }, "AgentWorkItem", workItemId.ToString(), cancellationToken);
    }

    public async Task ResumeAsync(int workItemId, int operatedByUserId, CancellationToken cancellationToken = default)
    {
        await EnsureSystemAdminAsync(operatedByUserId, cancellationToken);
        var now = AppTime.Now;
        var affected = await _context.AgentWorkItems
            .Where(item => item.Id == workItemId && item.Status == AgentWorkItemStatus.Paused)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AgentWorkItemStatus.Pending)
                .SetProperty(item => item.NextRunAt, now)
                .SetProperty(item => item.LockedAt, (DateTime?)null)
                .SetProperty(item => item.ErrorMessage, string.Empty)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (affected != 1) throw new InvalidOperationException("只有已暂停的工作项可以恢复");

        var snapshot = await _context.AgentWorkItems.AsNoTracking()
            .FirstAsync(item => item.Id == workItemId, cancellationToken);
        await _context.ToDoTasks
            .Where(task => task.Id == snapshot.TaskId && task.AgentDefinitionId == snapshot.AgentDefinitionId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(task => task.AgentExecutionStatus, AgentTaskExecutionStatus.Pending)
                .SetProperty(task => task.AgentLastError, string.Empty)
                .SetProperty(task => task.UpdatedAt, now), cancellationToken);

        await _eventBus.PublishAsync("agent.work-item.resumed", new { workItemId, operatedByUserId }, "AgentWorkItem", workItemId.ToString(), cancellationToken);
    }

    public async Task CancelAsync(int workItemId, int operatedByUserId, CancellationToken cancellationToken = default)
    {
        await EnsureSystemAdminAsync(operatedByUserId, cancellationToken);
        var now = AppTime.Now;
        var affected = await _context.AgentWorkItems
            .Where(item => item.Id == workItemId
                && (item.Status == AgentWorkItemStatus.Pending
                    || item.Status == AgentWorkItemStatus.Retrying
                    || item.Status == AgentWorkItemStatus.Paused
                    || item.Status == AgentWorkItemStatus.WaitingPlanConfirmation))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AgentWorkItemStatus.Cancelled)
                .SetProperty(item => item.ErrorMessage, "由系统管理员取消")
                .SetProperty(item => item.CompletedAt, now)
                .SetProperty(item => item.LockedAt, (DateTime?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (affected != 1) throw new InvalidOperationException("只有待执行、待确认计划、等待重试或已暂停的工作项可以取消");

        var snapshot = await _context.AgentWorkItems.AsNoTracking()
            .FirstAsync(item => item.Id == workItemId, cancellationToken);
        var hasOtherActive = await _context.AgentWorkItems.AsNoTracking()
            .AnyAsync(item => item.TaskId == snapshot.TaskId && ActiveStatuses.Contains(item.Status), cancellationToken);
        if (!hasOtherActive)
        {
            await _context.ToDoTasks
                .Where(task => task.Id == snapshot.TaskId && task.AgentDefinitionId == snapshot.AgentDefinitionId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(task => task.AgentExecutionStatus, AgentTaskExecutionStatus.None)
                    .SetProperty(task => task.AgentLastError, "由系统管理员取消 Agent 自动执行")
                    .SetProperty(task => task.UpdatedAt, now), cancellationToken);
        }

        await _eventBus.PublishAsync("agent.work-item.cancelled", new { workItemId, operatedByUserId }, "AgentWorkItem", workItemId.ToString(), cancellationToken);
    }

    public async Task<AgentWorkItem> RetryAsync(int workItemId, int operatedByUserId, CancellationToken cancellationToken = default)
    {
        await EnsureSystemAdminAsync(operatedByUserId, cancellationToken);
        var failed = await _context.AgentWorkItems.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == workItemId, cancellationToken)
            ?? throw new InvalidOperationException("Agent 工作项不存在");
        if (failed.Status != AgentWorkItemStatus.Failed)
            throw new InvalidOperationException("只有执行失败的工作项可以手动重试");
        var retryKey = $"manual-retry:{failed.Id}";
        if (await _context.AgentWorkItems.AsNoTracking().AnyAsync(item => item.IdempotencyKey == retryKey, cancellationToken))
            throw new InvalidOperationException("该失败记录已经创建过重试工作项，请对最新失败记录继续操作");
        var hasActive = await _context.AgentWorkItems.AsNoTracking()
            .AnyAsync(item => item.TaskId == failed.TaskId && ActiveStatuses.Contains(item.Status), cancellationToken);
        if (hasActive) throw new InvalidOperationException("该任务已有待处理的 Agent 工作项，不能重复重试");

        var retried = await EnqueueTaskAsync(
            failed.TaskId,
            failed.RequestedByUserId,
            AgentWorkTriggerType.ManualRetry,
            failed.Id,
            failed.Prompt,
            retryKey,
            failed.CauseChainId,
            cancellationToken)
            ?? throw new InvalidOperationException("任务已结束、Agent 已停用或指派已改变，无法重试");

        await _eventBus.PublishAsync(
            "agent.work-item.retried",
            new { sourceWorkItemId = failed.Id, workItemId = retried.Id, operatedByUserId },
            "AgentWorkItem",
            retried.Id.ToString(),
            cancellationToken);
        return retried;
    }

    /// <summary>为历史数据或请求提交后未显式入队的 Agent 指派补建工作项。</summary>
    public async Task EnsurePendingAssignmentsAsync(CancellationToken cancellationToken = default)
    {
        var tasks = await _context.ToDoTasks.AsNoTracking()
            .Include(item => item.Project)
            .Where(item => !item.IsDeleted
                && item.AssigneeType == TaskAssigneeType.DigitalEmployee
                && item.AgentDefinitionId.HasValue
                && item.AgentExecutionStatus == AgentTaskExecutionStatus.Pending
                && item.Status != TaskStatus.Completed
                && item.Status != TaskStatus.Cancelled)
            .OrderBy(item => item.UpdatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);

        foreach (var task in tasks)
        {
            var requesterId = task.Project?.LeaderUserId > 0 ? task.Project.LeaderUserId : task.CreatorId;
            await EnqueueTaskAsync(
                task.Id,
                requesterId,
                AgentWorkTriggerType.TaskAssigned,
                task.Id,
                BuildAssignmentPrompt(task),
                $"task-assigned:{task.Id}:{task.AgentDefinitionId}:{task.AgentAssignmentVersion}",
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>审批全部结束后恢复相同工作项，不创建重复 Session。</summary>
    public async Task ResumeResolvedApprovalsAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await _context.AgentWorkItems
            .Where(item => item.Status == AgentWorkItemStatus.WaitingApproval && item.AiSessionId.HasValue)
            .OrderBy(item => item.UpdatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0) return;

        var now = AppTime.Now;
        foreach (var item in candidates)
        {
            var stillWaiting = await _context.AgentToolCalls.AsNoTracking()
                .AnyAsync(call => call.AiSessionId == item.AiSessionId
                    && call.Status == AgentToolCallStatus.PendingApproval, cancellationToken);
            if (stillWaiting) continue;

            item.Status = AgentWorkItemStatus.Pending;
            item.WaitingApprovalRequestId = null;
            item.NextRunAt = now;
            item.UpdatedAt = now;
            var task = await _context.ToDoTasks.FirstOrDefaultAsync(task => task.Id == item.TaskId, cancellationToken);
            if (task != null && task.AgentExecutionStatus == AgentTaskExecutionStatus.WaitingApproval)
                task.AgentExecutionStatus = AgentTaskExecutionStatus.Pending;
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task RecoverStaleWorkItemsAsync(CancellationToken cancellationToken = default)
    {
        var staleBefore = AppTime.Now.Subtract(StaleWorkItemTimeout);
        var staleItems = await _context.AgentWorkItems
            .Where(item => item.Status == AgentWorkItemStatus.Running
                && item.LockedAt.HasValue
                && item.LockedAt < staleBefore)
            .Take(20)
            .ToListAsync(cancellationToken);
        if (staleItems.Count == 0) return;

        var now = AppTime.Now;
        foreach (var item in staleItems)
        {
            var exhausted = item.AttemptCount >= item.MaxAttempts;
            item.Status = exhausted ? AgentWorkItemStatus.Failed : AgentWorkItemStatus.Retrying;
            item.ErrorMessage = exhausted ? "后台执行超时，且已达到最大重试次数" : "后台执行中断，已自动恢复到重试队列";
            item.NextRunAt = exhausted ? now : now.AddMinutes(1);
            item.CompletedAt = exhausted ? now : null;
            item.LockedAt = null;
            // 领取与恢复使用数据库原子更新；同一 DbContext 中可能仍缓存领取前的 null，
            // 必须显式标记，否则 EF 会认为 LockedAt 未变化而把数据库旧锁保留下来。
            _context.Entry(item).Property(entry => entry.LockedAt).IsModified = true;
            item.UpdatedAt = now;
            var task = await _context.ToDoTasks.FirstOrDefaultAsync(task => task.Id == item.TaskId, cancellationToken);
            if (task != null)
            {
                task.AgentExecutionStatus = exhausted ? AgentTaskExecutionStatus.Failed : AgentTaskExecutionStatus.Retrying;
                task.AgentLastError = item.ErrorMessage;
            }
            if (exhausted)
                await _outcomes.RecordFailureAsync(item, item.ErrorMessage, cancellationToken);
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>通过条件更新原子领取一个工作项，多实例部署时也不会重复执行。</summary>
    public async Task<int?> ClaimNextAsync(CancellationToken cancellationToken = default)
    {
        var now = AppTime.Now;
        var candidate = await _context.AgentWorkItems.AsNoTracking()
            .Where(item => (item.Status == AgentWorkItemStatus.Pending
                    || (item.Status == AgentWorkItemStatus.Retrying && item.AttemptCount < item.MaxAttempts))
                && item.NextRunAt <= now
                && !_context.AgentWorkItems.Any(inFlight =>
                    inFlight.TaskId == item.TaskId
                    && inFlight.Id != item.Id
                    && InFlightStatuses.Contains(inFlight.Status))
                && !_context.AgentWorkItems.Any(earlier =>
                    earlier.TaskId == item.TaskId
                    && earlier.Id < item.Id
                    && ActiveStatuses.Contains(earlier.Status)))
            .OrderBy(item => item.NextRunAt)
            .ThenBy(item => item.Id)
            .Select(item => new { item.Id, item.TaskId })
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate == null) return null;

        var affected = await _context.AgentWorkItems
            .Where(item => item.Id == candidate.Id
                && (item.Status == AgentWorkItemStatus.Pending
                    || (item.Status == AgentWorkItemStatus.Retrying && item.AttemptCount < item.MaxAttempts))
                && item.NextRunAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AgentWorkItemStatus.Running)
                // 首次执行和真正的失败重试才消耗 AttemptCount；同一 Session 的多轮工具调用不消耗重试额度。
                .SetProperty(item => item.AttemptCount, item =>
                    item.AttemptCount == 0 || item.Status == AgentWorkItemStatus.Retrying
                        ? item.AttemptCount + 1
                        : item.AttemptCount)
                .SetProperty(item => item.LockedAt, now)
                .SetProperty(item => item.StartedAt, item => item.StartedAt ?? now)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (affected != 1) return null;

        // ExecuteUpdate bypasses EF change tracking. The work item may have been
        // enqueued earlier in this same hosted-service scope, leaving a tracked
        // Pending instance behind. Reload it now so ProcessAsync observes Running
        // instead of returning early and leaving the database row stuck.
        var trackedWorkItem = _context.ChangeTracker.Entries<AgentWorkItem>()
            .FirstOrDefault(entry => entry.Entity.Id == candidate.Id);
        if (trackedWorkItem != null)
            await trackedWorkItem.ReloadAsync(cancellationToken);

        // 领取成功后立即同步任务状态。即使进程随后中断，页面也不会错误地一直显示“Agent待执行”。
        await _context.ToDoTasks
            .Where(task => task.Id == candidate.TaskId
                && !task.IsDeleted
                && task.AssigneeType == TaskAssigneeType.DigitalEmployee
                && task.Status != TaskStatus.Completed
                && task.Status != TaskStatus.Cancelled)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(task => task.AgentExecutionStatus, AgentTaskExecutionStatus.Running)
                .SetProperty(task => task.AgentLastError, string.Empty)
                .SetProperty(task => task.UpdatedAt, now), cancellationToken);
        return candidate.Id;
    }

    public async Task ProcessAsync(int workItemId, CancellationToken cancellationToken = default)
    {
        var workItem = await _context.AgentWorkItems
            .Include(item => item.AgentDefinition)
            .Include(item => item.Task)
            .FirstOrDefaultAsync(item => item.Id == workItemId, cancellationToken);
        if (workItem == null || workItem.Status != AgentWorkItemStatus.Running) return;
        using var activity = _telemetry?.StartConsumer("agent.assignment.process", "assignment", workItem.Id, workItem.ProjectId, workItem.TaskId);
        var stopwatch = Stopwatch.StartNew();
        var stepSucceeded = false;

        var blockedByEarlierWork = await _context.AgentWorkItems.AsNoTracking()
            .AnyAsync(item => item.TaskId == workItem.TaskId
                && item.Id < workItem.Id
                && (item.Status == AgentWorkItemStatus.WaitingPlanConfirmation
                    || item.Status == AgentWorkItemStatus.WaitingApproval
                    || item.Status == AgentWorkItemStatus.Paused
                    || item.Status == AgentWorkItemStatus.Running), cancellationToken);
        if (blockedByEarlierWork)
        {
            workItem.Status = AgentWorkItemStatus.Pending;
            workItem.AttemptCount = Math.Max(0, workItem.AttemptCount - 1);
            workItem.NextRunAt = AppTime.Now.AddSeconds(5);
            workItem.LockedAt = null;
            workItem.UpdatedAt = AppTime.Now;
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            var task = workItem.Task;
            if (task == null
                || task.IsDeleted
                || task.Status is TaskStatus.Completed or TaskStatus.Cancelled
                || task.AssigneeType != TaskAssigneeType.DigitalEmployee
                || task.AgentDefinitionId != workItem.AgentDefinitionId
                || workItem.AgentDefinition == null
                || !workItem.AgentDefinition.IsEnabled)
            {
                await CancelInvalidWorkItemAsync(workItem, task, cancellationToken);
                return;
            }

            if (workItem.RequiresPlan && !workItem.AiSessionId.HasValue
                && (!workItem.PlanApprovedAt.HasValue || !AgentPlanningService.IsCurrent(workItem, task, workItem.AgentDefinition)))
            {
                if (workItem.TriggerType == AgentWorkTriggerType.TaskAssigned
                    && workItem.Prompt.Contains("只有任务描述明确要求把结果写回系统时才允许调用工具", StringComparison.Ordinal))
                    workItem.Prompt = BuildAssignmentPrompt(task);
                await _planning.GenerateAsync(workItem, task, workItem.AgentDefinition, cancellationToken,
                    continueAutomatically: !AgentPlanningService.RequiresHumanPlan(task, workItem));
                stepSucceeded = workItem.Status is AgentWorkItemStatus.WaitingPlanConfirmation or AgentWorkItemStatus.Pending;
                return;
            }

            task.AgentExecutionStatus = AgentTaskExecutionStatus.Running;
            task.AgentLastError = string.Empty;
            if (task.Status == TaskStatus.NotStarted)
            {
                task.SetStatus(TaskStatus.InProgress);
                task.StartTime ??= AppTime.Now;
            }
            task.UpdatedAt = AppTime.Now;
            await _context.SaveChangesAsync(cancellationToken);

            var previousToolCallId = await _context.AgentToolCalls.AsNoTracking()
                .Where(call => call.AiSessionId == workItem.AiSessionId)
                .MaxAsync(call => (int?)call.Id, cancellationToken) ?? 0;

            AiSession session;
            string response;
            var allowBusinessTools = AllowsBusinessToolCalls(task, workItem.Prompt)
                && AllowsBusinessToolCalls(task, workItem.Prompt, workItem.PlanFeedback);
            if (workItem.AiSessionId.HasValue)
            {
                var user = await _context.Users.FirstOrDefaultAsync(item => item.Id == workItem.RequestedByUserId, cancellationToken)
                    ?? throw new InvalidOperationException("Agent 工作项发起人不存在");
                var continuation = "系统自动继续执行上一轮工作。请读取工具结果：若目标已完成，请给出最终结论；若仍需操作，只调用必要工具，不要重复已经成功或已被拒绝的操作。";
                (session, response) = await _execution.ContinueAsync(
                    workItem.AiSessionId.Value,
                    continuation,
                    user,
                    cancellationToken,
                    allowAutoCompletionComment: false,
                    allowBusinessTools: allowBusinessTools,
                    allowWebSearch: AllowsWebSearch(task, $"{workItem.Prompt}\n{workItem.PlanFeedback}"));
            }
            else
            {
                (session, response) = await _execution.RunAsync(
                    workItem.AgentDefinition.AgentKey,
                    workItem.RequiresPlan
                        ? $"{workItem.Prompt}\n\n【执行计划 v{workItem.PlanRevision}】\n{workItem.ExecutionPlan}\n计划不授予额外权限。重要变更按工具策略审批，交付由系统检查，必要时转人工。"
                        : workItem.Prompt,
                    workItem.RequestedByUserId,
                    workItem.ProjectId,
                    workItem.TaskId,
                    cancellationToken,
                    allowAutoCompletionComment: false,
                    allowBusinessTools: allowBusinessTools,
                    allowWebSearch: AllowsWebSearch(task, $"{workItem.Prompt}\n{workItem.PlanFeedback}"),
                    agentVersion: workItem.RequiresPlan ? workItem.PlanAgentVersion : null,
                    onSessionStarted: async (startedSession, token) =>
                    {
                        workItem.AiSessionId = startedSession.Id;
                        await _context.SaveChangesAsync(token);
                    });
            }
            stepSucceeded = true;

            workItem.StepCount++;
            workItem.ResultSummary = response;
            workItem.ErrorMessage = string.Empty;
            workItem.LockedAt = null;
            workItem.UpdatedAt = AppTime.Now;

            var newToolCalls = await _context.AgentToolCalls.AsNoTracking()
                .Where(call => call.AiSessionId == session.Id && call.Id > previousToolCallId)
                .OrderBy(call => call.Id)
                .ToListAsync(cancellationToken);
            var pendingApproval = newToolCalls.FirstOrDefault(call => call.Status == AgentToolCallStatus.PendingApproval);
            if (pendingApproval != null)
            {
                workItem.Status = AgentWorkItemStatus.WaitingApproval;
                workItem.WaitingApprovalRequestId = pendingApproval.ApprovalRequestId;
                task.AgentExecutionStatus = AgentTaskExecutionStatus.WaitingApproval;
                task.AgentLastRunAt = AppTime.Now;
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }

            // context.read 只是每轮加载上下文时写入的审计记录，不代表 Agent 还需要下一轮处理。
            // 只有真正的业务工具调用才驱动续跑，避免纯文本结果因上下文审计被重复执行至 MaxSteps。
            var hasContinuationToolCall = newToolCalls.Any(call =>
                !string.Equals(call.ToolName, AgentContextService.ContextReadToolName, StringComparison.OrdinalIgnoreCase));
            if (hasContinuationToolCall && workItem.StepCount < workItem.MaxSteps)
            {
                workItem.Status = AgentWorkItemStatus.Pending;
                workItem.NextRunAt = AppTime.Now.AddSeconds(1);
                task.AgentExecutionStatus = AgentTaskExecutionStatus.Pending;
                task.AgentLastRunAt = AppTime.Now;
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }

            if (AllowsWebSearch(task, $"{workItem.Prompt}\n{workItem.PlanFeedback}"))
            {
                response = await AppendSearchEvidenceAsync(session.Id, response, cancellationToken);
                workItem.ResultSummary = response;
            }
            await CompleteWorkItemAsync(workItem, task, session, response, newToolCalls, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await MarkInterruptedAsync(workItemId, CancellationToken.None);
            }
            catch (Exception recoveryException)
            {
                _logger.LogError(recoveryException, "Agent 工作项 {WorkItemId} 在服务停止时释放执行锁失败", workItemId);
            }
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await MarkFailedOrRetryAsync(workItemId, ex, cancellationToken);
        }
        finally
        {
            stopwatch.Stop();
            _telemetry?.RecordExecution(
                "assignment",
                stepSucceeded,
                workItem.AttemptCount == 1 && workItem.StepCount <= 1
                    ? (workItem.StartedAt ?? AppTime.Now).Subtract(workItem.CreatedAt).TotalMilliseconds
                    : null,
                stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<string> AppendSearchEvidenceAsync(int sessionId, string response, CancellationToken ct)
    {
        var calls = await _context.AgentToolCalls.AsNoTracking()
            .Where(call => call.AiSessionId == sessionId && call.ToolName == "web.search")
            .OrderBy(call => call.Id).ToListAsync(ct);
        var hits = new List<AgentSearchHit>();
        foreach (var call in calls.Where(call => call.Status == AgentToolCallStatus.Executed))
        {
            try
            {
                var result = System.Text.Json.JsonSerializer.Deserialize<AgentSearchResult>(call.ResultJson);
                if (result != null) hits.AddRange(result.Results);
            }
            catch (System.Text.Json.JsonException) { /* 损坏记录不能作为可核验来源 */ }
        }
        if (hits.Count == 0)
        {
            var reason = calls.Count == 0 ? "本次未调用搜索工具"
                : calls.Any(call => call.Status == AgentToolCallStatus.Failed) ? "搜索调用失败，请查看工具记录"
                : "搜索未返回可用来源";
            return $"【联网核验未完成】{reason}。以下模型输出不代表已通过联网核验。\n\n{response}";
        }
        var sources = string.Join("\n", hits.DistinctBy(hit => hit.Url).Take(15).Select(hit => $"- {hit.Title}：{hit.Url}"));
        return $"{response}\n\n【实际检索来源（工具记录）】\n{sources}";
    }

    private async Task CompleteWorkItemAsync(
        AgentWorkItem workItem,
        ToDoTask task,
        AiSession session,
        string response,
        IReadOnlyCollection<AgentToolCall> finalTurnToolCalls,
        CancellationToken cancellationToken)
    {
        var runtimeDefinition = _registry == null
            ? workItem.AgentDefinition!
            : await _registry.GetForExecutionAsync(workItem.AgentDefinition!.AgentKey,
                session.AgentVersion, cancellationToken: cancellationToken) ?? workItem.AgentDefinition;
        var inputVersion = task.ConcurrencyVersion;
        var automatic = _automaticAcceptance == null
            ? new AutomaticAcceptanceResult(false, "自动验收服务不可用，交付保留供人工验收")
            : await _automaticAcceptance.EvaluateAsync(workItem, task, session, runtimeDefinition, response, cancellationToken);
        var currentTask = await _context.ToDoTasks.AsNoTracking().FirstAsync(t => t.Id == task.Id, cancellationToken);
        var currentWork = await _context.AgentWorkItems.AsNoTracking().FirstAsync(w => w.Id == workItem.Id, cancellationToken);
        if (currentWork.Status != AgentWorkItemStatus.Running || currentTask.ConcurrencyVersion != inputVersion
            || currentTask.IsDeleted || currentTask.Status is TaskStatus.Completed or TaskStatus.Cancelled
            || currentTask.AgentDefinitionId != workItem.AgentDefinitionId)
        {
            // 检查期间发生取消、改派或需求变化，不能把旧交付写成新任务的完成状态。
            await _context.Entry(task).ReloadAsync(cancellationToken);
            await _context.Entry(workItem).ReloadAsync(cancellationToken);
            if (workItem.Status == AgentWorkItemStatus.Running)
            {
                workItem.Status = AgentWorkItemStatus.Cancelled;
                workItem.ErrorMessage = "交付核对期间任务发生变化，旧结果未自动验收";
                workItem.CompletedAt = AppTime.Now;
                workItem.LockedAt = null;
                task.AgentExecutionStatus = task.Status == TaskStatus.Completed ? AgentTaskExecutionStatus.Completed : AgentTaskExecutionStatus.None;
            }
            AiSessionService.MarkSucceeded(session);
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }
        var currentAgent = await _context.AgentDefinitions.AsNoTracking().FirstAsync(a => a.Id == workItem.AgentDefinitionId, cancellationToken);
        if (!currentAgent.IsEnabled || AgentPlanningService.RuntimeVersion(currentAgent) != session.AgentVersion)
            automatic = new(false, "执行期间 Agent 已停用或配置已变化，需要核对交付");
        // Session、工作项、任务和最终评论必须在同一次 SaveChanges 中完成。
        // 这样核心状态不会停留在“Session 已结束、工作项仍运行”的半完成状态。
        AiSessionService.MarkSucceeded(session);
        var now = AppTime.Now;
        workItem.Status = AgentWorkItemStatus.Completed;
        workItem.CompletedAt = now;
        workItem.LockedAt = null;
        workItem.UpdatedAt = now;
        task.AgentExecutionStatus = automatic.Accepted ? AgentTaskExecutionStatus.Completed : AgentTaskExecutionStatus.AwaitingConfirmation;
        task.AgentLastRunAt = now;
        task.AgentLastError = string.Empty;
        task.SetStatus(automatic.Accepted ? TaskStatus.Completed : TaskStatus.PendingConfirmation);
        task.UpdatedAt = now;

        var receipt = await _outcomes.CreateDeliveryAsync(workItem, task, session, response, cancellationToken);
        await _outcomes.ApplyAutomaticReviewAsync(receipt, automatic, cancellationToken);

        var alreadyAddedComment = finalTurnToolCalls.Any(call =>
            call.ToolName == "task.add_comment" && call.Status == AgentToolCallStatus.Executed);
        if (runtimeDefinition.AutoCommentOnCompletion
            && !alreadyAddedComment
            && !string.IsNullOrWhiteSpace(response))
        {
            var content = $"【{runtimeDefinition.Name} 自动执行结果】\n{response.Trim()}";
            _context.TaskComments.Add(new TaskComment
            {
                TaskId = task.Id,
                AuthorId = workItem.RequestedByUserId,
                Content = content.Length > 2000 ? content[..2000] : content,
                IsAiGenerated = true,
                AgentKey = runtimeDefinition.AgentKey,
                AiSessionId = session.Id
            });
        }
        await _context.SaveChangesAsync(cancellationToken);

        if (automatic.Accepted) return; // 普通完成不再向验收人发送待办通知。
        try
        {
            var reviewerIds = await new AttentionRecipientService(_context).TaskAsync(task, ct: cancellationToken);
            await _notifications.NotifyManyAsync(
                reviewerIds,
                "Agent 任务待确认",
                $"任务「{task.Title}」已交付，需要你处理：{automatic.Reason}",
                "Review",
                $"/Tasks/Details/{task.Id}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception notificationException)
        {
            // 通知属于完成后的附属动作，失败不能把已经完成的 Agent 工作重新打回重试。
            // 清除失败通知留下的 Added 实体，避免同一作用域后续 SaveChanges 再次提交它们。
            foreach (var entry in _context.ChangeTracker.Entries<UserNotification>()
                         .Where(entry => entry.State == EntityState.Added))
                entry.State = EntityState.Detached;
            _logger.LogError(
                notificationException,
                "Agent 工作项 {WorkItemId} 已完成，但待审核通知发送失败",
                workItem.Id);
        }
    }

    private async Task CancelInvalidWorkItemAsync(AgentWorkItem workItem, ToDoTask? task, CancellationToken cancellationToken)
    {
        var now = AppTime.Now;
        workItem.Status = AgentWorkItemStatus.Cancelled;
        workItem.ErrorMessage = "任务已删除、结束、取消，或 Agent 指派已经改变";
        workItem.CompletedAt = now;
        workItem.LockedAt = null;
        workItem.UpdatedAt = now;
        if (task != null && task.AgentDefinitionId == workItem.AgentDefinitionId)
            task.AgentExecutionStatus = task.Status == TaskStatus.Completed
                ? AgentTaskExecutionStatus.Completed
                : AgentTaskExecutionStatus.None;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkFailedOrRetryAsync(int workItemId, Exception exception, CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        var item = await _context.AgentWorkItems.FirstOrDefaultAsync(work => work.Id == workItemId, cancellationToken);
        if (item == null) return;

        var message = string.IsNullOrWhiteSpace(exception.Message) ? "Agent 执行失败" : exception.Message.Trim();
        if (message.Length > 2000) message = message[..2000];
        var now = AppTime.Now;
        var exhausted = item.AttemptCount >= item.MaxAttempts;
        item.Status = exhausted ? AgentWorkItemStatus.Failed : AgentWorkItemStatus.Retrying;
        item.ErrorMessage = message;
        item.LockedAt = null;
        item.UpdatedAt = now;
        item.CompletedAt = exhausted ? now : null;
        item.NextRunAt = exhausted ? now : now.Add(GetRetryDelay(item.AttemptCount));

        var task = await _context.ToDoTasks.FirstOrDefaultAsync(task => task.Id == item.TaskId, cancellationToken);
        if (task != null)
        {
            task.AgentExecutionStatus = exhausted ? AgentTaskExecutionStatus.Failed : AgentTaskExecutionStatus.Retrying;
            task.AgentLastError = message;
            task.AgentLastRunAt = now;
        }
        if (exhausted)
            await _outcomes.RecordFailureAsync(item, message, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogWarning(exception, "Agent 工作项 {WorkItemId} 执行失败，第 {Attempt}/{MaxAttempts} 次", item.Id, item.AttemptCount, item.MaxAttempts);

        if (exhausted && task != null)
        {
            await _notifications.StageTaskAttentionAsync(task, "Blocked", "Agent 任务执行失败",
                $"任务「{task.Title}」自动重试仍失败：{message}", now, true, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task MarkInterruptedAsync(int workItemId, CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        var item = await _context.AgentWorkItems.FirstOrDefaultAsync(
            work => work.Id == workItemId && work.Status == AgentWorkItemStatus.Running,
            cancellationToken);
        if (item == null) return;

        var now = AppTime.Now;
        // 宿主停止不消耗重试额度；Pending 可以在尝试次数已达上限时恢复同一项工作。
        // 不重置 AttemptCount 或 Session，恢复后真实失败仍按原上限终止。
        item.Status = AgentWorkItemStatus.Pending;
        item.ErrorMessage = "Agent 服务停止，已释放执行锁并等待自动恢复";
        item.LockedAt = null;
        item.NextRunAt = now;
        item.UpdatedAt = now;

        var task = await _context.ToDoTasks.FirstOrDefaultAsync(task => task.Id == item.TaskId, cancellationToken);
        if (task != null)
        {
            task.AgentExecutionStatus = AgentTaskExecutionStatus.Pending;
            task.AgentLastError = item.ErrorMessage;
            task.AgentLastRunAt = now;
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static TimeSpan GetRetryDelay(int attemptCount) => attemptCount switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(15)
    };

    private async Task MarkEnqueueFailureAsync(
        ToDoTask task,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        task.AgentExecutionStatus = AgentTaskExecutionStatus.Failed;
        task.AgentLastError = errorMessage;
        task.UpdatedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogWarning(
            "Agent task {TaskId} could not be enqueued: {ErrorMessage}",
            task.Id,
            errorMessage);
    }

    private async Task EnsureSystemAdminAsync(int userId, CancellationToken cancellationToken)
    {
        var isSystemAdmin = await _context.Users.AsNoTracking()
            .AnyAsync(user => user.Id == userId && user.Role == UserRole.systemAdmin && user.Status == UserStatus.Active, cancellationToken);
        if (!isSystemAdmin) throw new UnauthorizedAccessException("只有启用的系统管理员可以管理 Agent 工作项");
    }

    public static string BuildAssignmentPrompt(ToDoTask task)
    {
        var deadline = task.EndTime?.ToString("yyyy-MM-dd HH:mm") ?? "未设置";
        return $"""
            你已被自动指派执行任务 #{task.Id}「{task.Title}」。
            任务描述：{(string.IsNullOrWhiteSpace(task.Description) ? "无" : task.Description.Trim())}
            优先级：{task.Priority}；截止时间：{deadline}。
            请读取当前项目和任务上下文，在授权范围内推进任务。只有任务描述明确要求把结果写回系统时才允许调用工具；只读、汇总、分析或建议类任务必须直接输出最终结果，不得调用 task.add_comment 或其他写入工具。普通新增记录使用已授权工具并由系统自动检查；修改重要数据等高风险操作仍按工具规则审批，不要为低风险操作反复要求人工确认。不得声称已完成实际上没有执行的外部操作。完成本轮后给出实际成果、可核验依据和未解决的问题。系统决定能否自动验收，不要自行宣布任务已经通过验收。
            """;
    }

    public static bool AllowsWebSearch(ToDoTask task, string? prompt = null)
    {
        var source = $"{task.Title}\n{task.Description}\n{prompt}";
        if (new[] { "禁止联网", "不要联网", "不联网", "不得调用工具", "no web", "offline" }
            .Any(marker => source.Contains(marker, StringComparison.OrdinalIgnoreCase))) return false;
        return new[] { "联网", "网上搜索", "网络搜索", "搜索公开", "检索公开", "web search", "search the web" }
            .Any(marker => source.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public static bool AllowsBusinessToolCalls(ToDoTask task, string? workItemPrompt = null, string? planFeedback = null)
    {
        var source = $"{task.Title}\n{task.Description ?? string.Empty}\n{planFeedback}";
        if (!string.IsNullOrWhiteSpace(workItemPrompt)
            && !workItemPrompt.Contains("只有任务描述明确要求把结果写回系统时才允许调用工具", StringComparison.Ordinal))
        {
            source = $"{source}\n{workItemPrompt}";
        }

        if (string.IsNullOrWhiteSpace(source)) return false;

        var readOnlyMarkers = new[]
        {
            "只读", "只读取", "不修改", "不要写入", "不得写入", "禁止写入", "不写回", "不得调用工具",
            "read-only", "readonly", "do not write", "don't write", "must not write", "without writing"
        };
        if (readOnlyMarkers.Any(marker => source.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return false;

        var writeMarkers = new[]
        {
            "写入", "写回", "添加评论", "新增", "创建", "修改", "更新", "删除", "调整", "指派", "分配", "变更", "提交审批",
            "生成报告", "生成日报", "生成周报", "保存到", "记录到系统", "同步到系统",
            "write", "comment", "create", "update", "delete", "assign", "change", "post", "save", "generate report"
        };
        return writeMarkers.Any(marker => source.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}

public class AgentAutomationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentAutomationHostedService> _logger;
    private readonly AgentQueueSignal _queueSignal;
    private readonly int _maxConcurrency;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _maxPollInterval;
    private readonly TimeSpan _maintenanceInterval;

    public AgentAutomationHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<AgentAutomationOptions> options,
        AgentQueueSignal queueSignal,
        ILogger<AgentAutomationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _queueSignal = queueSignal;
        _logger = logger;
        _maxConcurrency = Math.Clamp(options.Value.MaxConcurrency, 1, 16);
        _pollInterval = TimeSpan.FromMilliseconds(
            Math.Clamp(options.Value.PollIntervalMilliseconds, 100, 30_000));
        _maxPollInterval = TimeSpan.FromMilliseconds(
            Math.Clamp(options.Value.MaxPollIntervalMilliseconds,
                (int)_pollInterval.TotalMilliseconds,
                60_000));
        _maintenanceInterval = TimeSpan.FromSeconds(
            Math.Clamp(options.Value.MaintenanceIntervalSeconds, 1, 300));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, _maxConcurrency)
            .Select(workerIndex => RunWorkerAsync(workerIndex, stoppingToken))
            .Append(RunMaintenanceLoopAsync(stoppingToken));
        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(int workerIndex, CancellationToken stoppingToken)
    {
        // 奇偶 Worker 使用不同队列优先级，并在每轮后交换，避免自动任务或手工任务长期饥饿。
        var preferManualQueue = workerIndex % 2 == 1;
        var idleDelay = _pollInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = false;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<AgentWorkQueueService>();
                var manualQueue = scope.ServiceProvider.GetRequiredService<AgentRunQueueService>();

                if (preferManualQueue)
                {
                    handled = await TryProcessManualAsync(manualQueue, stoppingToken)
                        || await TryProcessAssignmentAsync(queue, stoppingToken);
                }
                else
                {
                    handled = await TryProcessAssignmentAsync(queue, stoppingToken)
                        || await TryProcessManualAsync(manualQueue, stoppingToken);
                }
                preferManualQueue = !preferManualQueue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent 后台 Worker {WorkerIndex} 发生异常", workerIndex);
            }

            if (!handled)
            {
                try
                {
                    var signaled = await _queueSignal.WaitAsync(idleDelay, stoppingToken);
                    idleDelay = signaled
                        ? _pollInterval
                        : TimeSpan.FromMilliseconds(Math.Min(
                            _maxPollInterval.TotalMilliseconds,
                            Math.Max(_pollInterval.TotalMilliseconds, idleDelay.TotalMilliseconds * 2)));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
            else
            {
                idleDelay = _pollInterval;
            }
        }
    }

    private async Task RunMaintenanceLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<AgentWorkQueueService>();
                var dispatcher = scope.ServiceProvider.GetRequiredService<AgentDispatchService>();
                var manualQueue = scope.ServiceProvider.GetRequiredService<AgentRunQueueService>();
                await queue.RecoverStaleWorkItemsAsync(stoppingToken);
                await queue.ResumeResolvedApprovalsAsync(stoppingToken);
                await dispatcher.EnsurePendingDispatchesAsync(stoppingToken);
                await queue.EnsurePendingAssignmentsAsync(stoppingToken);
                await manualQueue.RecoverStaleAsync(stoppingToken);
                await manualQueue.ResumeResolvedApprovalsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent 后台队列维护发生异常");
            }

            try
            {
                await Task.Delay(_maintenanceInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static async Task<bool> TryProcessAssignmentAsync(
        AgentWorkQueueService queue,
        CancellationToken cancellationToken)
    {
        var workItemId = await queue.ClaimNextAsync(cancellationToken);
        if (!workItemId.HasValue) return false;
        await queue.ProcessAsync(workItemId.Value, cancellationToken);
        return true;
    }

    private static async Task<bool> TryProcessManualAsync(
        AgentRunQueueService queue,
        CancellationToken cancellationToken)
    {
        var runJobId = await queue.ClaimNextAsync(cancellationToken);
        if (!runJobId.HasValue) return false;
        await queue.ProcessAsync(runJobId.Value, cancellationToken);
        return true;
    }
}

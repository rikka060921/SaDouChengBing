using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public class UserNotificationService
{
    private readonly ApplicationDbContext _context;

    public UserNotificationService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task NotifyAsync(int userId, string title, string content, string type = "Info", string? link = null)
    {
        await NotifyManyAsync([userId], title, content, type, link);
    }

    public async Task NotifyManyAsync(IEnumerable<int> userIds, string title, string content, string type = "Info", string? link = null)
    {
        var ids = userIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return;

        _context.UserNotifications.AddRange(ids.Select(userId => new UserNotification
        {
            UserId = userId,
            Title = title.Length > 200 ? title[..200] : title,
            Content = content.Length > 2000 ? content[..2000] : content,
            Type = type,
            Link = link,
            // 普通进展保留历史，但不制造需要人清理的未读数字。
            IsRead = IsQuiet(type),
            ReadAt = IsQuiet(type) ? AppTime.Now : null
        }));
        await _context.SaveChangesAsync();
    }

    public Task<List<UserNotification>> GetForUserAsync(int userId, bool unreadOnly = false)
    {
        var query = _context.UserNotifications
            .Where(n => n.UserId == userId);
        if (unreadOnly) query = query.Where(n => !n.IsRead);
        return query.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Take(100).ToListAsync();
    }

    public static bool IsQuiet(string type) => type is "Info" or "Success" or "Task";

    /// <summary>共享任务提醒键，会议督办和任务扫描对同一人同一天只生成一条同类提醒。</summary>
    public async Task<int> StageTaskAttentionAsync(ToDoTask task, string kind, string title, string content,
        DateTime now, bool escalate = false, CancellationToken ct = default)
    {
        var recipients = new AttentionRecipientService(_context);
        var ids = kind == "NeedsDefinition"
            ? (await recipients.ManagersAsync(task.ProjectId, ct)).Take(1).ToList()
            : await recipients.TaskAsync(task, escalate, ct);
        var link = $"/Tasks/Details/{task.Id}";
        var type = $"Task{kind}";
        var dayEnd = now.Date.AddDays(1);
        var existing = await _context.UserNotifications.AsNoTracking().Where(n => ids.Contains(n.UserId)
            && n.Link == link && n.Type == type && n.CreatedAt >= now.Date && n.CreatedAt < dayEnd)
            .Select(n => n.UserId).ToListAsync(ct);
        existing.AddRange(_context.ChangeTracker.Entries<UserNotification>()
            .Where(e => e.State == EntityState.Added && e.Entity.Link == link && e.Entity.Type == type
                && e.Entity.CreatedAt >= now.Date && e.Entity.CreatedAt < dayEnd).Select(e => e.Entity.UserId));
        ids = ids.Except(existing).ToList();
        _context.UserNotifications.AddRange(ids.Select(id => new UserNotification
        {
            UserId = id, Title = title.Length > 200 ? title[..200] : title,
            Content = content.Length > 2000 ? content[..2000] : content, Type = type, Link = link, CreatedAt = now
        }));
        return ids.Count;
    }

    public async Task MarkReadAsync(int id, int userId)
    {
        var notification = await _context.UserNotifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);
        if (notification == null) return;

        notification.IsRead = true;
        notification.ReadAt = AppTime.Now;
        await _context.SaveChangesAsync();
    }

    public async Task MarkAllReadAsync(int userId)
    {
        var now = AppTime.Now;
        var unreadItems = await _context.UserNotifications
            .Where(item => item.UserId == userId && !item.IsRead)
            .ToListAsync();
        foreach (var item in unreadItems)
        {
            item.IsRead = true;
            item.ReadAt = now;
        }
        await _context.SaveChangesAsync();
    }

    public async Task GenerateTaskRemindersAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        await using var lease = await new DistributedLeaseService(_context).TryAcquireAsync(
            "task-attention:all", TimeSpan.FromMinutes(10), cancellationToken);
        if (lease == null) return;
        var dueLimit = now.AddHours(24);
        var tasks = await _context.ToDoTasks.AsNoTracking()
            .Where(t => !t.IsDeleted && !t.IsCompleted
                && t.Status != ToDo.Entities.TaskStatus.Completed && t.Status != ToDo.Entities.TaskStatus.Cancelled
                && t.Project != null && !t.Project.IsDeleted && t.Project.Status == ProjectStatus.Active && t.EndTime.HasValue && t.EndTime <= dueLimit)
            .ToListAsync(cancellationToken);
        foreach (var task in tasks)
        {
            if (task.AgentExecutionStatus == AgentTaskExecutionStatus.WaitingApproval) continue;
            var meetingActions = await _context.MeetingActionItems.AsNoTracking()
                .Where(a => a.MatchedTaskId == task.Id && a.IsConfirmed && a.MeetingMinutes != null
                    && !a.MeetingMinutes.IsDeleted && a.MeetingMinutes.ConfirmedAt.HasValue).ToListAsync(cancellationToken);
            // 已由会议督办接管的任务不再另发一套提醒（包括已确认的暂缓/检查点）。
            if (meetingActions.Count > 0) continue;
            var overdue = task.EndTime < now;
            if (task.AssigneeType == TaskAssigneeType.DigitalEmployee && !overdue) continue;
            await StageTaskAttentionAsync(task, overdue ? "Overdue" : "DueSoon",
                overdue ? "任务已逾期" : "任务将在24小时内到期",
                $"任务「{task.Title}」截止 {task.EndTime:yyyy-MM-dd HH:mm}，请更新进展或处理阻塞。",
                now, overdue && task.EndTime <= now.AddDays(-1), cancellationToken);
        }
        await _context.SaveChangesAsync(cancellationToken);
    }
}

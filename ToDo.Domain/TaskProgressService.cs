using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;
using TaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Domain;

/// <summary>成员只推进自己的人工任务，不借此获得任务编辑、改派或自行验收权限。</summary>
public sealed class TaskProgressService(ApplicationDbContext context)
{
    public IQueryable<ToDoTask> MyActiveTasks(int userId) => context.ToDoTasks.Where(task =>
        !task.IsDeleted && !task.IsCompleted && task.AssigneeType == TaskAssigneeType.Human
        && (task.Status == TaskStatus.NotStarted || task.Status == TaskStatus.InProgress)
        && (task.AssigneeId == userId || (!task.AssigneeId.HasValue && task.ClaimerId == userId))
        && task.Project != null && !task.Project.IsDeleted
        && context.Users.Any(user => user.Id == userId && !user.IsDeleted && user.Status == UserStatus.Active
            && (user.Role == UserRole.systemAdmin || task.Project.LeaderUserId == userId
                || context.ProjectUsers.Any(member => member.ProjectId == task.ProjectId && member.UserId == userId))));

    public Task<bool> CanUpdateAsync(int taskId, int userId, CancellationToken ct = default)
        => MyActiveTasks(userId).AnyAsync(task => task.Id == taskId, ct);

    public async Task SaveAsync(int taskId, int userId, int expectedVersion, int progress,
        string? note, bool submitForReview, CancellationToken ct = default)
    {
        var task = await MyActiveTasks(userId).SingleOrDefaultAsync(item => item.Id == taskId, ct)
            ?? throw new UnauthorizedAccessException("当前任务不可由你更新，请刷新页面检查任务状态和负责人。");
        if (task.ConcurrencyVersion != expectedVersion)
            throw new InvalidOperationException("任务已被其他操作更新，请先复制本次说明，再重新打开任务查看最新进展。");
        if (progress is < 0 or > 99) throw new InvalidOperationException("执行进度应在 0—99% 之间，验收通过后自动变为 100%。");
        note = note?.Trim() ?? string.Empty;
        if (note.Length > 1500) throw new InvalidOperationException("进展或成果说明不能超过 1500 字。");
        if (submitForReview && string.IsNullOrWhiteSpace(note))
            throw new InvalidOperationException("请简要说明已交付的成果及核验方式，再提交验收。");
        if (!submitForReview && task.Progress == progress && note.Length == 0) return;

        // 先确认接收人，再暂存业务修改，避免失败后调用方保存了半条提交。
        List<int> reviewers = [];
        if (submitForReview)
        {
            var pending = new ToDoTask
            {
                ProjectId = task.ProjectId, CreatorId = task.CreatorId,
                ReviewerId = task.ReviewerId, AssigneeId = task.AssigneeId,
                AssigneeType = task.AssigneeType
            };
            pending.SetStatus(TaskStatus.PendingConfirmation);
            reviewers = await new AttentionRecipientService(context).TaskAsync(pending, ct: ct);
            if (reviewers.Count == 0) throw new InvalidOperationException("暂时没有有效审核人，请联系项目负责人设置后再提交。");
        }
        var user = await context.Users.AsNoTracking().SingleAsync(item => item.Id == userId, ct);
        var before = $"状态：{task.Status}；进度：{task.Progress}%";
        task.SetStatus(submitForReview ? TaskStatus.PendingConfirmation : TaskStatus.InProgress,
            submitForReview ? 99 : progress);
        task.UpdatedAt = AppTime.Now;
        if (note.Length > 0) context.TaskComments.Add(new TaskComment
        {
            TaskId = task.Id, AuthorId = userId,
            Content = (submitForReview ? "提交验收：" : "进展更新：") + note
        });
        if (submitForReview)
        {
            foreach (var reviewer in reviewers) context.UserNotifications.Add(new UserNotification
            {
                UserId = reviewer, Title = "任务待审核", Content = $"任务「{task.Title}」已提交成果，请进行验收。",
                Type = "Review", Link = $"/Tasks/Details/{task.Id}", IsRead = false
            });
        }
        context.ChangeLogs.Add(new ChangeLog
        {
            OperationType = OperationType.状态变更, OperationTarget = OperationTarget.任务,
            OperatedAt = AppTime.Now, OperatedByUserId = userId, OperatedByUserName = user.UserName,
            OperatedByRealName = user.RealName, ProjectId = task.ProjectId, TaskId = task.Id,
            TaskTitle = task.Title, TargetId = task.Id, BeforeContent = before,
            AfterContent = $"状态：{task.Status}；进度：{task.Progress}%\n{note}"
        });
        // 状态、成果说明、日志和验收提醒一次提交，版本冲突不会留下半条业务记录。
        await context.SaveChangesAsync(ct);
    }
}

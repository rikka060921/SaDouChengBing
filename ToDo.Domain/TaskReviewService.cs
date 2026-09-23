using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;
using TaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Domain;

/// <summary>议题验收复用任务审核权限，所有状态和证据由调用方一次性提交。</summary>
public sealed class TaskReviewService(ApplicationDbContext context)
{
    public async Task<bool> CanReviewAsync(ToDoTask task, ApplicationUser user)
    {
        var project = await context.Project.AsNoTracking().FirstOrDefaultAsync(p => p.Id == task.ProjectId && !p.IsDeleted);
        if (project == null) return false;
        var privileged = user.Role == UserRole.systemAdmin || project.LeaderUserId == user.Id
            || await context.ProjectUsers.AnyAsync(p => p.ProjectId == task.ProjectId && p.UserId == user.Id && p.ProjectRole == (int)ProjectRole.Admin);
        return CanReviewTask(task, user, privileged);
    }

    public static bool CanReviewTask(ToDoTask task, ApplicationUser user, bool privileged)
        => !task.IsDeleted && !user.IsDeleted && user.Status == UserStatus.Active
            && (privileged || (task.ReviewerId == user.Id && task.CreatorId != user.Id));

    public async Task<bool> StageAgendaApprovalAsync(ToDoTask task, ApplicationUser user, string comment)
    {
        if (!await CanReviewAsync(task, user)) return false;
        // 同一任务可以关联多个议题；一次请求中只记录一次验收。
        if (task.Status == TaskStatus.Completed && task.IsCompleted) return true;
        return await StageReviewAsync(task, user, true, comment) != null;
    }

    /// <summary>暂存审核结果；调用方负责在同一事务内保存，以及驳回后的 Agent 重新入队。</summary>
    public async Task<TaskComment?> StageReviewAsync(ToDoTask task, ApplicationUser user, bool approved, string comment)
    {
        if (!await CanReviewAsync(task, user) || task.Status != TaskStatus.PendingConfirmation) return null;

        var now = AppTime.Now;
        var before = task.Status;
        task.ApplyReviewDecision(approved);
        task.UpdatedAt = now;
        var review = new TaskComment { TaskId = task.Id, AuthorId = user.Id, Content = comment };
        context.TaskComments.Add(review);
        await new AgentOutcomeService(context).ApplyHumanReviewAsync(task.Id, approved, user.Id, comment);
        var workItems = await context.AgentWorkItems.Where(w => w.TaskId == task.Id
            && (w.Status == AgentWorkItemStatus.Pending || w.Status == AgentWorkItemStatus.Retrying
                || w.Status == AgentWorkItemStatus.Running || w.Status == AgentWorkItemStatus.WaitingApproval
                || w.Status == AgentWorkItemStatus.Paused || w.Status == AgentWorkItemStatus.WaitingPlanConfirmation)).ToListAsync();
        foreach (var work in workItems)
        {
            work.Status = AgentWorkItemStatus.Cancelled;
            work.CompletedAt = now;
            work.LockedAt = null;
            work.UpdatedAt = now;
        }
        context.ChangeLogs.Add(new ChangeLog
        {
            OperationType = OperationType.状态变更, OperationTarget = OperationTarget.任务,
            OperatedAt = now, OperatedByUserId = user.Id, OperatedByUserName = user.UserName,
            OperatedByRealName = user.RealName, ProjectId = task.ProjectId, TaskId = task.Id,
            TaskTitle = task.Title, TargetId = task.Id,
            BeforeContent = $"状态：{before}", AfterContent = $"状态：{task.Status}\n审核意见：{comment}"
        });
        var quiet = approved || (task.AssigneeType == TaskAssigneeType.DigitalEmployee && task.ReworkCount < 2);
        var stakeholders = approved
            ? new[] { task.CreatorId, task.AssigneeId ?? 0, task.ReviewerId ?? 0 }.Where(id => id > 0).Distinct().ToList()
            : await new AttentionRecipientService(context).TaskAsync(task, task.ReworkCount >= 2);
        stakeholders.Remove(user.Id);
        foreach (var id in stakeholders)
            context.UserNotifications.Add(new UserNotification
            {
                UserId = id, Title = approved ? "任务审核通过" : "任务被打回返工",
                Content = approved ? $"任务「{task.Title}」已审核通过。{comment}" : $"任务「{task.Title}」被打回，累计返工 {task.ReworkCount} 次。{comment}",
                Type = approved ? "Success" : quiet ? "Task" : "Warning", Link = $"/Tasks/Details/{task.Id}", CreatedAt = now,
                IsRead = quiet, ReadAt = quiet ? now : null
            });
        return review;
    }
}

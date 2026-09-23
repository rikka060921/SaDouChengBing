using Microsoft.EntityFrameworkCore;
using ToDo.Entities;

namespace ToDo.Context;

public sealed record ProjectArchiveCheck(int UnfinishedTasks, IReadOnlyList<string> Blockers)
{
    public bool CanArchive => Blockers.Count == 0;
}

/// <summary>Shared business boundary; this does not replace the caller's access checks.</summary>
public static class ProjectLifecycleRules
{
    public const string ReadOnlyMessage = "项目已归档，历史内容只读。请先由项目管理员恢复项目。";

    public static async Task RequireActiveAsync(ApplicationDbContext db, int projectId, CancellationToken ct = default)
    {
        var status = await db.Project.AsNoTracking().Where(p => p.Id == projectId && !p.IsDeleted)
            .Select(p => (ProjectStatus?)p.Status).SingleOrDefaultAsync(ct);
        if (status == ProjectStatus.Archived) throw new InvalidOperationException(ReadOnlyMessage);
        if (status == null) throw new InvalidOperationException("项目不存在或已删除。");
    }

    public static async Task<ProjectArchiveCheck> CheckArchiveAsync(ApplicationDbContext db, int projectId, CancellationToken ct = default)
    {
        var blockers = new List<string>();
        if (await db.AgentWorkItems.AnyAsync(w => w.ProjectId == projectId &&
            w.Status != AgentWorkItemStatus.Completed && w.Status != AgentWorkItemStatus.Failed && w.Status != AgentWorkItemStatus.Cancelled, ct))
            blockers.Add("存在未结束的 Agent 工作（含排队、执行、重试、暂停或等待确认），请先完成或取消。");
        if (await db.AgentRunJobs.AnyAsync(w => w.ProjectId == projectId &&
            w.Status != AgentRunJobStatus.Completed && w.Status != AgentRunJobStatus.Failed && w.Status != AgentRunJobStatus.Cancelled, ct)
            || await db.AgentEventExecutions.AnyAsync(w => w.ProjectId == projectId &&
            (w.Status == AgentEventExecutionStatus.Pending || w.Status == AgentEventExecutionStatus.Running ||
             w.Status == AgentEventExecutionStatus.Retrying || w.Status == AgentEventExecutionStatus.WaitingApproval), ct))
            blockers.Add("存在未结束的 Agent 自动化执行，请先处理。");
        if (await db.ApprovalRequests.AnyAsync(a => a.ProjectId == projectId &&
            (a.Status == ApprovalRequestStatus.Pending || a.Status == ApprovalRequestStatus.Processing), ct)
            || await db.AgentToolCalls.AnyAsync(t => t.Status == AgentToolCallStatus.PendingApproval &&
                t.Session != null && t.Session.ProjectId == projectId, ct))
            blockers.Add("存在待审批或执行中的敏感操作，请先处理。");
        if (await db.ToDoTasks.AnyAsync(t => t.ProjectId == projectId && !t.IsDeleted && t.Status == ToDo.Entities.TaskStatus.PendingConfirmation, ct))
            blockers.Add("存在待验收任务，请先验收或退回。");
        if (await db.ScheduledJobs.AnyAsync(j => j.ProjectId == projectId && j.IsRunning, ct))
            blockers.Add("项目定时任务正在执行，请稍后重试。");
        var unfinished = await db.ToDoTasks.CountAsync(t => t.ProjectId == projectId && !t.IsDeleted &&
            t.Status != ToDo.Entities.TaskStatus.Completed && t.Status != ToDo.Entities.TaskStatus.Cancelled, ct);
        return new ProjectArchiveCheck(unfinished, blockers);
    }
}

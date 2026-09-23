using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

/// <summary>通知只找实际处理人；管理权限不意味着接收项目内每一条提醒。</summary>
public sealed class AttentionRecipientService(ApplicationDbContext context)
{
    public async Task<List<int>> ManagersAsync(int projectId, CancellationToken ct = default)
    {
        var project = await context.Project.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && !p.IsDeleted, ct);
        if (project == null) return [];
        return await context.Users.AsNoTracking().Where(u => !u.IsDeleted && u.Status == UserStatus.Active
            && (u.Id == project.LeaderUserId || u.Role == UserRole.systemAdmin
                || context.ProjectUsers.Any(m => m.ProjectId == projectId && m.UserId == u.Id && m.ProjectRole == (int)ProjectRole.Admin)))
            .OrderBy(u => u.Id == project.LeaderUserId ? 0 : u.Role == UserRole.systemAdmin ? 2 : 1)
            .ThenBy(u => u.Id).Select(u => u.Id).ToListAsync(ct);
    }

    public async Task<List<int>> TaskAsync(ToDoTask task, bool escalate = false, CancellationToken ct = default)
    {
        if (task.IsDeleted || task.IsCompleted || task.Status is ToDo.Entities.TaskStatus.Completed or ToDo.Entities.TaskStatus.Cancelled) return [];
        var project = await context.Project.AsNoTracking().FirstOrDefaultAsync(p => p.Id == task.ProjectId && !p.IsDeleted, ct);
        if (project == null) return [];
        var managers = await ManagersAsync(task.ProjectId, ct);
        var candidates = task.Status == ToDo.Entities.TaskStatus.PendingConfirmation
            ? new[] { task.ReviewerId ?? 0 }
            : task.AssigneeType == TaskAssigneeType.Human
                ? new[] { task.AssigneeId ?? 0, task.CreatorId }
                : new[] { task.CreatorId, task.ReviewerId ?? 0 };
        var eligible = await context.Users.AsNoTracking().Where(u => candidates.Contains(u.Id)
            && !u.IsDeleted && u.Status == UserStatus.Active
            && (u.Role == UserRole.systemAdmin || u.Id == project.LeaderUserId
                || context.ProjectUsers.Any(m => m.ProjectId == task.ProjectId && m.UserId == u.Id)))
            .Select(u => u.Id).ToListAsync(ct);
        var primary = candidates.FirstOrDefault(id => eligible.Contains(id)
            && (task.Status != ToDo.Entities.TaskStatus.PendingConfirmation || id != task.CreatorId || managers.Contains(id)));
        if (primary == 0) primary = managers.FirstOrDefault();
        var result = primary > 0 ? new List<int> { primary } : [];
        if (escalate)
        {
            var backup = managers.FirstOrDefault(id => id != primary);
            if (backup > 0) result.Add(backup);
        }
        return result;
    }
}

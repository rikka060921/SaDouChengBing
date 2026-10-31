using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;
using TaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Domain;

public sealed record TeamProjectAttention(int ProjectId, string ProjectName, int Overdue, int WaitingReview, int Blocked);
public sealed record ProjectGettingStarted(int ProjectId, bool HasMembers, bool HasDocuments, bool HasTask, bool HasAuthorizedAgent);

/// <summary>Read-only views of existing work, not another task or notification lifecycle.</summary>
public sealed class TeamWorkbenchService(ApplicationDbContext context)
{
    public async Task<List<ToDoTask>> MyWorkAsync(int userId, CancellationToken ct = default)
        => await new TaskProgressService(context).MyActiveTasks(userId).AsNoTracking().Include(t => t.Project)
            .OrderBy(t => t.EndTime == null).ThenBy(t => t.EndTime).ThenBy(t => t.Priority).ThenBy(t => t.Id)
            .Take(6).ToListAsync(ct);

    public async Task<List<TeamProjectAttention>> ManagedAttentionAsync(int userId, DateTime now, CancellationToken ct = default)
    {
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && !u.IsDeleted && u.Status == UserStatus.Active, ct);
        if (user == null) return [];
        var projects = context.Project.AsNoTracking().Where(p => !p.IsDeleted && p.Status == ProjectStatus.Active
            && (user.Role == UserRole.systemAdmin || p.LeaderUserId == userId || context.ProjectUsers.Any(m => m.ProjectId == p.Id && m.UserId == userId && m.ProjectRole == (int)ProjectRole.Admin)));
        var counts = await projects.Select(p => new TeamProjectAttention(p.Id, p.Name,
            context.ToDoTasks.Count(t => t.ProjectId == p.Id && !t.IsDeleted && !t.IsCompleted && t.Status != TaskStatus.Completed && t.Status != TaskStatus.Cancelled && t.EndTime < now),
            context.ToDoTasks.Count(t => t.ProjectId == p.Id && !t.IsDeleted && t.Status == TaskStatus.PendingConfirmation),
            context.ToDoTasks.Count(t => t.ProjectId == p.Id && !t.IsDeleted && !t.IsCompleted && t.Status != TaskStatus.Completed && t.Status != TaskStatus.Cancelled
                && (t.AgentExecutionStatus == AgentTaskExecutionStatus.Failed || t.AgentExecutionStatus == AgentTaskExecutionStatus.Paused || t.ReworkCount >= 2))))
            .ToListAsync(ct);
        return counts.Where(p => p.Overdue + p.WaitingReview + p.Blocked > 0).OrderByDescending(p => p.Blocked).ThenByDescending(p => p.Overdue).ThenBy(p => p.ProjectId).Take(5).ToList();
    }

    public async Task<ProjectGettingStarted?> GettingStartedAsync(int projectId, int userId, CancellationToken ct = default)
    {
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && !u.IsDeleted && u.Status == UserStatus.Active, ct);
        if (user == null) return null;
        var project = await context.Project.AsNoTracking().SingleOrDefaultAsync(p => p.Id == projectId && !p.IsDeleted && p.Status == ProjectStatus.Active
            && (user.Role == UserRole.systemAdmin || p.LeaderUserId == userId || context.ProjectUsers.Any(m => m.ProjectId == p.Id && m.UserId == userId && m.ProjectRole == (int)ProjectRole.Admin)), ct);
        if (project == null) return null;
        return new(projectId,
            await context.ProjectUsers.AnyAsync(m => m.ProjectId == projectId && m.UserId != project.LeaderUserId && m.User != null && !m.User.IsDeleted && m.User.Status == UserStatus.Active, ct),
            await context.ProjectDocuments.AnyAsync(d => d.ProjectId == projectId && d.IsCurrent, ct),
            await context.ToDoTasks.AnyAsync(t => t.ProjectId == projectId && !t.IsDeleted, ct),
            await context.AgentDocumentPermissions.AnyAsync(p => p.ProjectId == projectId && p.CanRead && context.AgentDefinitions.Any(a => a.AgentKey == p.AgentKey && a.IsEnabled), ct));
    }
}

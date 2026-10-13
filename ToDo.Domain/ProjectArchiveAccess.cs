using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public static class ProjectArchiveAccess
{
    // A public project overview does not make its uploaded documents public.
    public static Task<bool> CanReadAsync(ApplicationDbContext context, int projectId, int userId,
        UserRole role, CancellationToken cancellationToken = default)
    {
        if (projectId <= 0 || userId <= 0) return Task.FromResult(false);
        return context.Project.AsNoTracking().AnyAsync(p => p.Id == projectId && !p.IsDeleted
            && (role == UserRole.systemAdmin || p.LeaderUserId == userId
                || context.ProjectUsers.Any(m => m.ProjectId == projectId && m.UserId == userId)), cancellationToken);
    }
}

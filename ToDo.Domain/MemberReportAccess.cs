using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

/// <summary>历史汇总可读范围与新增汇总范围分开；不扩大原项目管理员权限。</summary>
public static class MemberReportAccess
{
    public static IQueryable<Project> Projects(ApplicationDbContext context, ApplicationUser user, bool activeOnly = false)
        => context.Project.Where(p => !p.IsDeleted && (!activeOnly || p.Status == ProjectStatus.Active)
            && (user.Role == UserRole.systemAdmin || context.ProjectUsers.Any(m => m.ProjectId == p.Id
                && m.UserId == user.Id && m.ProjectRole == (int)ProjectRole.Admin)));
}

using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

/// <summary>Members may upload ordinary materials, but cannot replace the project's maintained historical evidence.</summary>
public static class ProjectHistoricalDocumentPolicy
{
    public static bool IsReserved(string fileName) => string.Equals(fileName,ProjectDevelopmentLedger.FileName,StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName,ProjectArchivePackage.FileName,StringComparison.OrdinalIgnoreCase);

    public static async Task RequireManagerAsync(ApplicationDbContext context,int projectId,int userId,string fileName,CancellationToken ct=default)
    {
        if(!IsReserved(fileName))return;
        var actor=await context.Users.AsNoTracking().SingleOrDefaultAsync(u=>u.Id==userId&&!u.IsDeleted&&u.Status==UserStatus.Active,ct);
        if(actor==null||!await context.Project.AnyAsync(p=>p.Id==projectId&&!p.IsDeleted&&(actor.Role==UserRole.systemAdmin||p.LeaderUserId==userId
            ||context.ProjectUsers.Any(m=>m.ProjectId==projectId&&m.UserId==userId&&m.ProjectRole==(int)ProjectRole.Admin)),ct))
            throw new UnauthorizedAccessException("历史档案和开发记录只能由项目管理人员维护，普通项目资料上传权限不等于修改历史来源的权限");
    }
}

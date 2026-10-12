using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;
using Xunit;

namespace ToDo.Test;

public sealed class ProjectLeaderTransferTests
{
    [Fact]
    public async Task System_admin_can_transfer_active_project_to_an_active_member()
    {
        await using var db = await CreateContextAsync();
        var domain = new ProjectDomain(db, NullLogger<ProjectDomain>.Instance);

        Assert.True(await domain.UpdateProjectLeaderAsync(20, 2, 1, UserRole.systemAdmin));

        Assert.Equal(2, (await db.Project.SingleAsync()).LeaderUserId);
        var member = await db.ProjectUsers.SingleAsync(item => item.ProjectId == 20 && item.UserId == 2);
        Assert.Equal((int)ProjectRole.Admin, member.ProjectRole);
        Assert.True(member.IsProjectAdmin);
        Assert.Contains(await db.ChangeLogs.ToListAsync(), item => item.ProjectId == 20
            && item.BeforeContent?.Contains("项目负责人: 1") == true
            && item.AfterContent?.Contains("项目负责人: 2") == true);
    }

    [Fact]
    public async Task Regular_member_cannot_transfer_project_leadership()
    {
        await using var db = await CreateContextAsync();
        var domain = new ProjectDomain(db, NullLogger<ProjectDomain>.Instance);

        Assert.False(await domain.UpdateProjectLeaderAsync(20, 2, 2, UserRole.teamMember));
        Assert.Equal(1, (await db.Project.SingleAsync()).LeaderUserId);
    }

    [Fact]
    public async Task Archived_project_cannot_transfer_leadership()
    {
        await using var db = await CreateContextAsync();
        (await db.Project.SingleAsync()).Status = ProjectStatus.Archived;
        await db.SaveChangesAsync();
        var domain = new ProjectDomain(db, NullLogger<ProjectDomain>.Instance);

        Assert.False(await domain.UpdateProjectLeaderAsync(20, 2, 1, UserRole.systemAdmin));
        Assert.Equal(1, (await db.Project.SingleAsync()).LeaderUserId);
    }

    private static async Task<ApplicationDbContext> CreateContextAsync()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new ApplicationDbContext(options);
        db.Users.AddRange(
            new ApplicationUser { Id = 1, UserName = "creator", RealName = "创建者", Role = UserRole.systemAdmin },
            new ApplicationUser { Id = 2, UserName = "member", RealName = "接任者", Role = UserRole.teamMember });
        db.Project.Add(new Project
        {
            Id = 20,
            Name = "正式项目",
            CreatedByUserId = 1,
            LeaderUserId = 1,
            Status = ProjectStatus.Active
        });
        db.ProjectUsers.AddRange(
            new ProjectUser { ProjectId = 20, UserId = 1, ProjectRole = (int)ProjectRole.Admin },
            new ProjectUser { ProjectId = 20, UserId = 2, ProjectRole = (int)ProjectRole.Member });
        await db.SaveChangesAsync();
        return db;
    }
}

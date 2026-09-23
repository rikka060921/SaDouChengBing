using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Test;

public class ProjectVisibilityTests
{
    [Fact]
    public async Task PublicProject_IsVisibleToAuthenticatedTeamMember()
    {
        await using var context = CreateContext();
        context.Project.Add(NewProject(1, '0', leaderUserId: 10));
        await context.SaveChangesAsync();

        var service = CreateService(context);

        Assert.True(await service.CanViewProjectAsync(1, 99, UserRole.teamMember));
    }

    [Fact]
    public async Task ProtectedProject_IsVisibleOnlyToLeaderOrMember()
    {
        await using var context = CreateContext();
        context.Project.Add(NewProject(2, '1', leaderUserId: 10));
        context.ProjectUsers.Add(new ProjectUser { ProjectId = 2, UserId = 20 });
        await context.SaveChangesAsync();

        var service = CreateService(context);

        Assert.True(await service.CanViewProjectAsync(2, 10, UserRole.teamMember));
        Assert.True(await service.CanViewProjectAsync(2, 20, UserRole.teamMember));
        Assert.False(await service.CanViewProjectAsync(2, 99, UserRole.teamMember));
    }

    [Fact]
    public async Task PrivateProject_IsVisibleOnlyToLeaderForTeamMembers()
    {
        await using var context = CreateContext();
        context.Project.Add(NewProject(3, '2', leaderUserId: 10));
        context.ProjectUsers.Add(new ProjectUser
        {
            ProjectId = 3,
            UserId = 20,
            ProjectRole = (int)ProjectRole.Admin
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);

        Assert.True(await service.CanViewProjectAsync(3, 10, UserRole.teamMember));
        Assert.False(await service.CanViewProjectAsync(3, 20, UserRole.teamMember));
    }

    [Fact]
    public async Task SystemAdmin_CanViewPrivateProject()
    {
        await using var context = CreateContext();
        context.Project.Add(NewProject(4, '2', leaderUserId: 10));
        await context.SaveChangesAsync();

        var service = CreateService(context);

        Assert.True(await service.CanViewProjectAsync(4, 99, UserRole.systemAdmin));
    }

    [Fact]
    public async Task DeletedProject_IsNeverVisible()
    {
        await using var context = CreateContext();
        var project = NewProject(5, '0', leaderUserId: 10);
        project.IsDeleted = true;
        context.Project.Add(project);
        await context.SaveChangesAsync();

        var service = CreateService(context);

        Assert.False(await service.CanViewProjectAsync(5, 10, UserRole.systemAdmin));
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options);
    }

    private static ProjectDomain CreateService(ApplicationDbContext context) =>
        new(context, NullLogger<ProjectDomain>.Instance);

    private static Project NewProject(int id, char visibility, int leaderUserId) => new()
    {
        Id = id,
        Name = $"Project-{id}",
        CreatedByUserId = leaderUserId,
        LeaderUserId = leaderUserId,
        IsEncrypted = visibility
    };
}

using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Test;

public class OperationLogScopeTests
{
    [Fact]
    public async Task ProjectMember_OnlySeesLogsFromAccessibleProjects()
    {
        await using var context = CreateContext();
        var member = new ApplicationUser { Id = 1, UserName = "member", RealName = "Member", Status = UserStatus.Active };
        var other = new ApplicationUser { Id = 2, UserName = "other", RealName = "Other", Status = UserStatus.Active };
        context.Users.AddRange(member, other);
        context.Project.AddRange(
            NewProject(10, "Visible Project", 2),
            NewProject(20, "Hidden Project", 2));
        context.ProjectUsers.Add(new ProjectUser { ProjectId = 10, UserId = 1 });
        context.ChangeLogs.AddRange(
            NewLog(100, 10, "Visible Project", 1, "Member"),
            NewLog(200, 20, "Hidden Project", 2, "Other"),
            NewLog(300, null, null, 2, "Other"));
        await context.SaveChangesAsync();

        var service = new OperationLogService(context);
        var logs = await service.GetPagedLogsAsync(new OperationLogService.LogQueryParameters(), member);

        Assert.Single(logs);
        Assert.Equal(100, logs[0].Id);
        Assert.Equal(["Visible Project"], await service.GetDistinctProjectsAsync(member));
        Assert.Equal(["Member"], await service.GetDistinctUsersAsync(member));
        Assert.Null(await service.GetLogDetailsAsync(200, new OperationLogService.LogQueryParameters(), member));
    }

    [Fact]
    public async Task ProjectLeader_CanViewLogsWithoutMembershipRow()
    {
        await using var context = CreateContext();
        var leader = new ApplicationUser { Id = 7, UserName = "leader", Status = UserStatus.Active };
        context.Users.Add(leader);
        context.Project.Add(NewProject(70, "Led Project", 7));
        context.ChangeLogs.Add(NewLog(700, 70, "Led Project", 7, "Leader"));
        await context.SaveChangesAsync();

        var service = new OperationLogService(context);

        Assert.True(await service.CanViewLogsAsync(leader));
        Assert.Single(await service.GetPagedLogsAsync(new OperationLogService.LogQueryParameters(), leader));
    }

    [Fact]
    public async Task SystemAdministrator_CanViewProjectAndSystemLogs()
    {
        await using var context = CreateContext();
        var admin = new ApplicationUser
        {
            Id = 9,
            UserName = "admin",
            Status = UserStatus.Active,
            Role = UserRole.systemAdmin
        };
        context.Users.Add(admin);
        context.Project.Add(NewProject(90, "Any Project", 9));
        context.ChangeLogs.AddRange(
            NewLog(900, 90, "Any Project", 9, "Admin"),
            NewLog(901, null, null, 9, "Admin"));
        await context.SaveChangesAsync();

        var service = new OperationLogService(context);
        var logs = await service.GetPagedLogsAsync(new OperationLogService.LogQueryParameters(), admin);

        Assert.True(await service.CanViewLogsAsync(admin));
        Assert.Equal(2, logs.Count);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options);
    }

    private static Project NewProject(int id, string name, int leaderId) => new()
    {
        Id = id,
        Name = name,
        CreatedByUserId = leaderId,
        LeaderUserId = leaderId,
        Status = ProjectStatus.Active
    };

    private static ChangeLog NewLog(
        int id,
        int? projectId,
        string? projectName,
        int operatedByUserId,
        string realName) => new()
    {
        Id = id,
        ProjectId = projectId,
        ProjectName = projectName,
        OperatedByUserId = operatedByUserId,
        OperatedByRealName = realName,
        OperationType = OperationType.更新,
        OperationTarget = OperationTarget.项目,
        OperatedAt = DateTime.UtcNow
    };
}

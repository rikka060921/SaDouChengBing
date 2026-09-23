using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;
using TaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Test;

public sealed class TaskProgressTests
{
    [Fact]
    public async Task MissingReviewerCannotLeaveStagedPartialSubmission()
    {
        await using var f = await Fixture.CreateAsync();
        f.Leader.Status = UserStatus.Inactive;
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SaveAsync(f.Task.Id, f.Owner.Id, f.Task.ConcurrencyVersion, 60, "成果", true));
        Assert.Equal(TaskStatus.NotStarted, f.Task.Status);
        Assert.False(f.Db.ChangeTracker.HasChanges());
        await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Db.TaskComments.ToListAsync());
        Assert.Empty(await f.Db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task EmptyUnchangedProgressIsNoOpAndLongNoteIsRejected()
    {
        await using var f = await Fixture.CreateAsync();
        var version = f.Task.ConcurrencyVersion;
        await f.Service.SaveAsync(f.Task.Id, f.Owner.Id, version, 0, "  ", false);
        Assert.Equal(version, f.Task.ConcurrencyVersion);
        Assert.Equal(TaskStatus.NotStarted, f.Task.Status);
        Assert.Empty(await f.Db.ChangeLogs.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SaveAsync(f.Task.Id, f.Owner.Id, version, 30, new string('字', 1501), false));
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task OwnerCanProgressSubmitAndReviewerCanApproveWithoutGivingOwnerEditRights()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Service.SaveAsync(f.Task.Id, f.Owner.Id, f.Task.ConcurrencyVersion, 45, "已完成手机页面", false);
        Assert.Equal(TaskStatus.InProgress, f.Task.Status);
        Assert.Equal(45, f.Task.Progress);
        Assert.Equal(f.Leader.Id, f.Task.CreatorId);
        Assert.Equal(f.Owner.Id, f.Task.AssigneeId);
        Assert.Equal("交付反馈表单", f.Task.Title);
        Assert.Empty(await f.Db.UserNotifications.ToListAsync());
        Assert.Single(await f.Db.TaskComments.ToListAsync());
        await f.Service.SaveAsync(f.Task.Id, f.Owner.Id, f.Task.ConcurrencyVersion, 45, "三条样例已保存，按说明核验", true);
        Assert.Equal(TaskStatus.PendingConfirmation, f.Task.Status);
        Assert.Equal(99, f.Task.Progress);
        Assert.False(f.Task.IsCompleted);
        Assert.False(await f.Service.CanUpdateAsync(f.Task.Id, f.Owner.Id));
        var notification = Assert.Single(await f.Db.UserNotifications.ToListAsync());
        Assert.Equal(f.Leader.Id, notification.UserId);
        Assert.False(notification.IsRead);
        var reviews = new TaskReviewService(f.Db);
        Assert.Null(await reviews.StageReviewAsync(f.Task, f.Owner, true, "自行通过"));
        Assert.NotNull(await reviews.StageReviewAsync(f.Task, f.Leader, true, "验证三条样例通过"));
        await f.Db.SaveChangesAsync();
        Assert.Equal(100, f.Task.Progress);
        Assert.True(f.Task.IsCompleted);
    }

    [Theory]
    [InlineData("other-member")]
    [InlineData("creator-not-owner")]
    [InlineData("removed")]
    [InlineData("disabled")]
    [InlineData("deleted-user")]
    [InlineData("deleted-project")]
    [InlineData("deleted-task")]
    [InlineData("reassigned")]
    [InlineData("digital")]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("reviewing")]
    public async Task IneligibleRequestDoesNotWriteAnything(string reason)
    {
        await using var f = await Fixture.CreateAsync();
        var userId = f.Owner.Id;
        if (reason == "other-member") userId = f.Other.Id;
        if (reason == "creator-not-owner") userId = f.Leader.Id;
        if (reason == "removed") f.Db.ProjectUsers.Remove(await f.Db.ProjectUsers.SingleAsync(m => m.UserId == f.Owner.Id));
        if (reason == "disabled") f.Owner.Status = UserStatus.Inactive;
        if (reason == "deleted-user") f.Owner.IsDeleted = true;
        if (reason == "deleted-project") f.Project.IsDeleted = true;
        if (reason == "deleted-task") f.Task.IsDeleted = true;
        if (reason == "reassigned") { f.Task.ClaimerId = f.Owner.Id; f.Task.AssigneeId = f.Other.Id; }
        if (reason == "digital") f.Task.AssigneeType = TaskAssigneeType.DigitalEmployee;
        if (reason == "completed") f.Task.SetStatus(TaskStatus.Completed);
        if (reason == "cancelled") f.Task.SetStatus(TaskStatus.Cancelled);
        if (reason == "reviewing") f.Task.SetStatus(TaskStatus.PendingConfirmation);
        await f.Db.SaveChangesAsync();
        Assert.False(await f.Service.CanUpdateAsync(f.Task.Id, userId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveAsync(f.Task.Id, userId, f.Task.ConcurrencyVersion, 40, "越权", false));
        Assert.Empty(await f.Db.TaskComments.ToListAsync());
        Assert.Empty(await f.Db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task StaleVersionCannotOverwriteProgressOrDuplicateSubmission()
    {
        await using var f = await Fixture.CreateAsync();
        var version = f.Task.ConcurrencyVersion;
        await f.Service.SaveAsync(f.Task.Id, f.Owner.Id, version, 60, "保留此进度", false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SaveAsync(f.Task.Id, f.Owner.Id, version, 20, "旧页面", false));
        Assert.Equal(60, f.Task.Progress);
        Assert.Single(await f.Db.TaskComments.ToListAsync());
        await f.Service.SaveAsync(f.Task.Id, f.Owner.Id, f.Task.ConcurrencyVersion, 60, "成果就绪", true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveAsync(f.Task.Id, f.Owner.Id, f.Task.ConcurrencyVersion, 60, "重复提交", true));
        Assert.Single(await f.Db.UserNotifications.ToListAsync());
    }

    [Theory]
    [InlineData(-1, "说明", false)]
    [InlineData(100, "说明", false)]
    [InlineData(50, "", true)]
    [InlineData(50, "  ", true)]
    public async Task InvalidInputCannotChangeTask(int progress, string note, bool submit)
    {
        await using var f = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SaveAsync(f.Task.Id, f.Owner.Id, f.Task.ConcurrencyVersion, progress, note, submit));
        Assert.Equal(TaskStatus.NotStarted, f.Task.Status);
        Assert.Empty(await f.Db.TaskComments.ToListAsync());
    }

    [Fact]
    public async Task MyWorkIncludesOnlyOwnActionableTasksAndClaimFallback()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.Equal(f.Task.Id, (await f.Service.MyActiveTasks(f.Owner.Id).SingleAsync()).Id);
        Assert.Empty(await f.Service.MyActiveTasks(f.Other.Id).ToListAsync());
        Assert.Empty(await f.Service.MyActiveTasks(f.Leader.Id).ToListAsync());
        f.Task.AssigneeId = null;
        f.Task.ClaimerId = f.Owner.Id;
        await f.Db.SaveChangesAsync();
        Assert.True(await f.Service.CanUpdateAsync(f.Task.Id, f.Owner.Id));
        f.Task.SetStatus(TaskStatus.PendingConfirmation);
        await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Service.MyActiveTasks(f.Owner.Id).ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required ApplicationDbContext Db { get; init; }
        public required ApplicationUser Leader { get; init; }
        public required ApplicationUser Owner { get; init; }
        public required ApplicationUser Other { get; init; }
        public required Project Project { get; init; }
        public required ToDoTask Task { get; init; }
        public TaskProgressService Service => new(Db);
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var leader = new ApplicationUser { UserName = "leader", NormalizedUserName = "LEADER", RealName = "负责人" };
            var owner = new ApplicationUser { UserName = "owner", NormalizedUserName = "OWNER", RealName = "开发成员" };
            var other = new ApplicationUser { UserName = "other", NormalizedUserName = "OTHER", RealName = "运营成员" };
            db.AddRange(leader, owner, other); await db.SaveChangesAsync();
            var project = new Project { Name = "三人协作", CreatedByUserId = leader.Id, LeaderUserId = leader.Id };
            db.Add(project); await db.SaveChangesAsync();
            db.ProjectUsers.AddRange(new ProjectUser { ProjectId = project.Id, UserId = owner.Id, ProjectRole = 1 },
                new ProjectUser { ProjectId = project.Id, UserId = other.Id, ProjectRole = 1 });
            var task = new ToDoTask { Title = "交付反馈表单", ProjectId = project.Id, CreatorId = leader.Id, ReviewerId = leader.Id, AssigneeId = owner.Id };
            db.Add(task); await db.SaveChangesAsync();
            return new() { Connection = connection, Db = db, Leader = leader, Owner = owner, Other = other, Project = project, Task = task };
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }
}

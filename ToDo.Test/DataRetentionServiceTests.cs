using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.Options;
using ToDo.Entities;

namespace ToDo.Test;

public sealed class DataRetentionServiceTests
{
    [Fact]
    public async Task PreviewAndRun_ArchivesOnlyTerminalSessionsAndDeletesOnlyOldReadNotifications()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();

        var now = AppTime.Now;
        var user = new ApplicationUser { UserName = "retention-user", Role = UserRole.teamMember };
        context.Users.Add(user);
        context.AiSessions.AddRange(
            new AiSession
            {
                AgentKey = "old-terminal",
                Status = AiSessionStatus.Succeeded,
                LastActivityAt = now.AddDays(-200),
                CompletedAt = now.AddDays(-200)
            },
            new AiSession
            {
                AgentKey = "old-waiting-human",
                Status = AiSessionStatus.WaitingHuman,
                LastActivityAt = now.AddDays(-200)
            },
            new AiSession
            {
                AgentKey = "recent-terminal",
                Status = AiSessionStatus.Succeeded,
                LastActivityAt = now.AddDays(-10),
                CompletedAt = now.AddDays(-10)
            });
        await context.SaveChangesAsync();
        context.UserNotifications.AddRange(
            new UserNotification
            {
                UserId = user.Id,
                Title = "old-read",
                Content = "delete",
                IsRead = true,
                ReadAt = now.AddDays(-100),
                CreatedAt = now.AddDays(-110)
            },
            new UserNotification
            {
                UserId = user.Id,
                Title = "old-unread",
                Content = "keep",
                IsRead = false,
                CreatedAt = now.AddDays(-110)
            },
            new UserNotification
            {
                UserId = user.Id,
                Title = "recent-read",
                Content = "keep",
                IsRead = true,
                ReadAt = now.AddDays(-5)
            });
        await context.SaveChangesAsync();

        var options = Options.Create(new DataRetentionOptions
        {
            ArchiveAiSessionsAfterDays = 180,
            DeleteReadNotificationsAfterDays = 90,
            BatchSize = 100
        });
        var service = new DataRetentionService(context, new DistributedLeaseService(context), options);

        var preview = await service.PreviewAsync(now);
        var result = await service.RunAsync(user.Id, "manual", now);

        Assert.Equal(1, preview.SessionsToArchive);
        Assert.Equal(1, preview.NotificationsToDelete);
        Assert.True(result.Executed);
        Assert.Equal(1, result.ArchivedSessionCount);
        Assert.Equal(1, result.DeletedNotificationCount);
        Assert.NotNull(await context.AiSessions.AsNoTracking()
            .Where(item => item.AgentKey == "old-terminal")
            .Select(item => item.ArchivedAt)
            .SingleAsync());
        Assert.Null(await context.AiSessions.AsNoTracking()
            .Where(item => item.AgentKey == "old-waiting-human")
            .Select(item => item.ArchivedAt)
            .SingleAsync());
        Assert.Equal(2, await context.UserNotifications.CountAsync());
        var audit = Assert.Single(await context.DataRetentionRuns.AsNoTracking().ToListAsync());
        Assert.Equal(DataRetentionRunStatus.Completed, audit.Status);
        Assert.Equal(user.Id, audit.TriggeredByUserId);
    }

    [Fact]
    public async Task Run_WhenLeaseIsHeld_SkipsWithoutMutatingData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        var leaseService = new DistributedLeaseService(context);
        await using var heldLease = await leaseService.TryAcquireAsync("data-retention:daily", TimeSpan.FromMinutes(5));
        Assert.NotNull(heldLease);
        var service = new DataRetentionService(context, leaseService, Options.Create(new DataRetentionOptions()));

        var result = await service.RunAsync(null, "scheduled");

        Assert.False(result.Executed);
        Assert.Empty(await context.DataRetentionRuns.ToListAsync());
    }
}

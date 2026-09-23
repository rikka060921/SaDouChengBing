using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Test;

public sealed class TaskSplitSubmissionTests
{
    [Fact]
    public async Task ConcurrentRequests_CommitOnlyOneBatch()
    {
        await using var fixture = await Database.CreateAsync();
        var id = Guid.NewGuid();
        async Task<int> Submit()
        {
            await using var context = fixture.CreateContext();
            return await new TaskSplitSubmissionService(context).SaveAsync(1, id, [NewTask()]);
        }
        var results = await Task.WhenAll(Task.Run(Submit), Task.Run(Submit));
        Assert.All(results, count => Assert.Equal(1, count));
        await using var verification = fixture.CreateContext();
        Assert.Equal(1, await verification.ToDoTasks.CountAsync());
        Assert.Equal(1, await verification.TaskSplitSubmissions.CountAsync());
    }

    [Fact]
    public async Task FailedBatch_RollsBackReceiptAndTasks_AndCanBeRetried()
    {
        await using var fixture = await Database.CreateAsync();
        var id = Guid.NewGuid();
        await using (var context = fixture.CreateContext())
        {
            var invalid = NewTask();
            invalid.ProjectId = 99999;
            await Assert.ThrowsAsync<DbUpdateException>(() => new TaskSplitSubmissionService(context).SaveAsync(1, id, [NewTask(), invalid]));
        }
        await using var verification = fixture.CreateContext();
        Assert.Empty(await verification.ToDoTasks.ToListAsync());
        Assert.Empty(await verification.TaskSplitSubmissions.ToListAsync());
        Assert.Equal(1, await new TaskSplitSubmissionService(verification).SaveAsync(1, id, [NewTask()]));
    }

    private static ToDoTask NewTask() => new() { Title = "拆分任务", ProjectId = 1, CreatorId = 1 };

    private sealed class Database(SqliteConnection anchor, string connectionString) : IAsyncDisposable
    {
        public ApplicationDbContext CreateContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connectionString).Options);
        public static async Task<Database> CreateAsync()
        {
            var connectionString = $"Data Source=split-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=10;Pooling=False";
            var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var fixture = new Database(anchor, connectionString);
            await using var context = fixture.CreateContext();
            await context.Database.EnsureCreatedAsync();
            context.Users.Add(new ApplicationUser { Id = 1, UserName = "reviewer" });
            context.Project.Add(new Project { Id = 1, Name = "测试项目", LeaderUserId = 1, CreatedByUserId = 1 });
            await context.SaveChangesAsync();
            return fixture;
        }
        public ValueTask DisposeAsync() => anchor.DisposeAsync();
    }
}

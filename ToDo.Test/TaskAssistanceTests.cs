using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;
using TaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Test;

public sealed partial class AgentFrameworkCompletionTests
{
    [Theory]
    [InlineData("next_steps")]
    [InlineData("risk_check")]
    [InlineData("progress_update")]
    public async Task TaskAssistance_MemberGetsTaskContextWithoutChangingFormalWork(string purpose)
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        Assert.True(await service.CanAssistAsync(task.Id, db.Member));
        var job = await service.RequestAsync(task.Id, purpose, db.Member, Guid.NewGuid());
        Assert.Equal(job.Id, await db.Queue.ClaimNextAsync());
        await db.Queue.ProcessAsync(job.Id);

        var latest = await service.GetLatestAsync(task.Id, db.Member);
        Assert.Equal(AgentRunJobStatus.Completed, latest!.Status);
        Assert.Equal("测试完成", latest.ResultSummary);
        Assert.True(AgentSessionExecutionPolicy.IsTaskAssistance(latest.AiSession!));
        Assert.Contains(purpose, latest.AiSession!.MetadataJson);
        Assert.Contains("设计成员工作台", db.AI.LastCompletionPrompt);
        Assert.Contains("已完成接口草稿", db.AI.LastCompletionPrompt);
        Assert.Empty(db.AI.LastCompletionOptions!.Tools);
        Assert.Equal(1, db.AI.CompletionCalls);
        Assert.Single(await db.Context.TaskComments.ToListAsync());
        Assert.Empty(await db.Context.AgentWorkItems.ToListAsync());
        Assert.Empty(await db.Context.AgentDeliveryReceipts.ToListAsync());
        var savedTask = await db.Context.ToDoTasks.AsNoTracking().SingleAsync();
        Assert.Equal(db.Member.Id, savedTask.AssigneeId);
        Assert.Equal(TaskStatus.InProgress, savedTask.Status);
        Assert.Equal(25, savedTask.Progress);
        Assert.All(await db.Context.AgentToolCalls.ToListAsync(), call => Assert.Equal("context.read", call.ToolName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskAssistance_BlocksNativeAndLegacyToolsAndAutoComments(bool native)
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        db.AI.SetCompletion(new AICompletionResult
        {
            Content = native ? "" : "仅建议，不应执行。<agent-actions>[{\"tool\":\"task.add_comment\",\"arguments\":{\"content\":\"未经授权写入\"}}]</agent-actions>",
            ToolCalls = native ? [new AICompletionToolCall
            {
                Id = "forbidden-write", Name = "task_add_comment", ArgumentsJson = "{\"content\":\"未经授权写入\"}"
            }] : []
        });
        var service = new TaskAssistanceService(db.Context, db.Queue);
        var job = await service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid());
        await db.Queue.ClaimNextAsync();
        await db.Queue.ProcessAsync(job.Id);
        var latest = await service.GetLatestAsync(task.Id, db.Member);
        Assert.Equal(native ? AgentRunJobStatus.Retrying : AgentRunJobStatus.Completed, latest!.Status);
        if (native)
        {
            Assert.Empty(latest.ResultSummary);
            Assert.Equal(AiSessionStatus.Failed, latest.AiSession!.Status);
            Assert.Contains("未返回可用的协助内容", latest.ErrorMessage);
        }
        Assert.DoesNotContain("agent-actions", latest.ResultSummary);
        Assert.Single(await db.Context.TaskComments.ToListAsync());
        Assert.Empty(await db.Context.ApprovalRequests.ToListAsync());
        Assert.All(await db.Context.AgentToolCalls.ToListAsync(), call => Assert.Equal("context.read", call.ToolName));
        Assert.Empty(db.AI.LastCompletionOptions!.Tools);
    }

    [Fact]
    public async Task TaskAssistance_ContinuationCannotUpgradePolicyEvenWithExplicitAllowFlags()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        var job = await service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid());
        await db.Queue.ClaimNextAsync();
        await db.Queue.ProcessAsync(job.Id);
        db.AI.SetCompletion(new AICompletionResult
        {
            Content = "<agent-actions>[{\"tool\":\"task.add_comment\",\"arguments\":{\"content\":\"升级权限\"}}]</agent-actions>",
            ToolCalls = [new AICompletionToolCall { Id = "search", Name = "web_search", ArgumentsJson = "{\"query\":\"secret\"}" }]
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Execution.ContinueAsync(job.AiSessionId, "请联网并写入评论", db.Member,
            allowAutoCompletionComment: true, allowBusinessTools: true, allowWebSearch: true));
        Assert.Equal(AiSessionStatus.Failed, (await db.Sessions.GetDetailsAsync(job.AiSessionId, db.Member))!.Status);
        Assert.Single(await db.Context.TaskComments.ToListAsync());
        Assert.Empty(db.AI.LastCompletionOptions!.Tools);
        Assert.All(await db.Context.AgentToolCalls.ToListAsync(), call => Assert.Equal("context.read", call.ToolName));
        var continuation = await db.Queue.EnqueueContinuationAsync(job.AiSessionId, "继续整理草稿", db.Member);
        await db.Queue.ClaimNextAsync();
        await db.Queue.ProcessAsync(continuation.Id);
        Assert.Equal(AgentRunJobStatus.Completed, (await service.GetLatestAsync(task.Id, db.Member))!.Status);
        Assert.Single(await db.Context.TaskComments.ToListAsync());
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("<agent-actions>[{\"tool\":\"task.add_comment\",\"arguments\":{\"content\":\"不能写入\"}}]</agent-actions>")]
    public async Task TaskAssistance_EmptyResultRetriesAndRecoversWhenModelReturnsUsefulAdvice(string emptyResponse)
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        db.AI.SetCompletion(new AICompletionResult { Content = emptyResponse });
        var job = await service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid());
        Assert.Equal(job.Id, await db.Queue.ClaimNextAsync());
        await db.Queue.ProcessAsync(job.Id);
        var retrying = await service.GetLatestAsync(task.Id, db.Member);
        Assert.Equal(AgentRunJobStatus.Retrying, retrying!.Status);
        Assert.Equal(1, retrying.AttemptCount);
        Assert.Empty(retrying.ResultSummary);
        Assert.Equal(0, retrying.AiSession!.TurnCount);
        Assert.Null(await db.Queue.ClaimNextAsync());

        db.AI.SetCompletion(new AICompletionResult { Content = "下一步：验证现有接口草稿的输入和输出。" });
        await db.Context.AgentRunJobs.Where(item => item.Id == job.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextRunAt, AppTime.Now.AddSeconds(-1)));
        Assert.Equal(job.Id, await db.Queue.ClaimNextAsync());
        await db.Queue.ProcessAsync(job.Id);
        var recovered = await service.GetLatestAsync(task.Id, db.Member);
        Assert.Equal(AgentRunJobStatus.Completed, recovered!.Status);
        Assert.Equal(2, recovered.AttemptCount);
        Assert.Contains("验证现有接口草稿", recovered.ResultSummary);
        Assert.Empty(recovered.ErrorMessage);
        Assert.Single(await db.Context.TaskComments.ToListAsync());
        Assert.Equal(TaskStatus.InProgress, (await db.Context.ToDoTasks.AsNoTracking().SingleAsync()).Status);
        Assert.All(await db.Context.AgentToolCalls.ToListAsync(), call => Assert.Equal("context.read", call.ToolName));
    }

    [Fact]
    public async Task TaskAssistance_PersistentlyEmptyResultStopsAfterAttemptLimitWithoutFalseSuccess()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        db.AI.SetCompletion(new AICompletionResult { Content = "" });
        var job = await service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid());
        for (var attempt = 1; attempt <= job.MaxAttempts; attempt++)
        {
            await db.Context.AgentRunJobs.Where(item => item.Id == job.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextRunAt, AppTime.Now.AddSeconds(-1)));
            Assert.Equal(job.Id, await db.Queue.ClaimNextAsync());
            await db.Queue.ProcessAsync(job.Id);
            var current = await service.GetLatestAsync(task.Id, db.Member);
            Assert.Equal(attempt == job.MaxAttempts ? AgentRunJobStatus.Failed : AgentRunJobStatus.Retrying, current!.Status);
            Assert.Equal(attempt, current.AttemptCount);
            Assert.Empty(current.ResultSummary);
            Assert.Equal(AiSessionStatus.Failed, current.AiSession!.Status);
            Assert.Equal(0, current.AiSession.TurnCount);
        }
        Assert.Null(await db.Queue.ClaimNextAsync());
        Assert.Equal(job.MaxAttempts, db.AI.CompletionCalls);
        Assert.Single(await db.Context.TaskComments.ToListAsync());
        Assert.Empty(await db.Context.AgentDeliveryReceipts.ToListAsync());
        Assert.Equal(TaskStatus.InProgress, (await db.Context.ToDoTasks.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task TaskAssistance_RejectsUnknownPurpose(string purpose)
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TaskAssistanceService(db.Context, db.Queue)
            .RequestAsync(task.Id, purpose, db.Member, Guid.NewGuid()));
        Assert.Empty(await db.Context.AiSessions.ToListAsync());
    }

    [Fact]
    public async Task TaskAssistance_RejectsEmptyRequestIdAndDisabledOrMissingAgent()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync(task.Id, "next_steps", db.Member, Guid.Empty));
        db.Agent.IsEnabled = false;
        await db.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid()));
        db.Agent.AgentKey = "different-agent";
        db.Agent.IsEnabled = true;
        await db.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid()));
        Assert.Empty(await db.Context.AiSessions.ToListAsync());
    }

    [Theory]
    [InlineData(TaskStatus.Completed)]
    [InlineData(TaskStatus.Cancelled)]
    public async Task TaskAssistance_CompletedTaskKeepsOwnHistoryButDisallowsNewWork(TaskStatus status)
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        var job = await service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid());
        task.SetStatus(status);
        await db.Context.SaveChangesAsync();
        Assert.False(await service.CanAssistAsync(task.Id, db.Member));
        Assert.NotNull(await service.GetLatestAsync(task.Id, db.Member));
        Assert.NotNull(await db.Sessions.GetDetailsAsync(job.AiSessionId, db.Member));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.Queue.EnqueueContinuationAsync(job.AiSessionId, "继续", db.Member));
    }

    [Fact]
    public async Task TaskAssistance_OnlyCurrentResponsibleMembersCanRequest()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        task.AssigneeId = null;
        await db.Context.SaveChangesAsync();
        Assert.False(await service.CanAssistAsync(task.Id, db.Member));
        task.ReviewerId = db.Member.Id;
        await db.Context.SaveChangesAsync();
        Assert.True(await service.CanAssistAsync(task.Id, db.Member));
        task.ReviewerId = null;
        task.CreatorId = db.Member.Id;
        await db.Context.SaveChangesAsync();
        Assert.True(await service.CanAssistAsync(task.Id, db.Member));
        await db.Context.ProjectUsers.Where(item => item.UserId == db.Member.Id).ExecuteDeleteAsync();
        Assert.False(await service.CanAssistAsync(task.Id, db.Member));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RequestAsync(task.Id, "risk_check", db.Member, Guid.NewGuid()));
    }

    [Fact]
    public async Task TaskAssistance_ProjectManagersAndLeadersMayAssistButNotReadOthersPrivateResults()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        var job = await service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid());
        Assert.Null(await service.GetLatestAsync(task.Id, db.Admin));
        Assert.Null(await db.Sessions.GetDetailsAsync(job.AiSessionId, db.Admin));
        Assert.Empty(await db.Sessions.GetRecentAsync(db.Admin));
        task.AssigneeId = null;
        var member = await db.Context.ProjectUsers.SingleAsync();
        member.ProjectRole = 0;
        await db.Context.SaveChangesAsync();
        Assert.True(await service.CanAssistAsync(task.Id, db.Member));
        member.ProjectRole = 1;
        db.Project.LeaderUserId = db.Member.Id;
        await db.Context.SaveChangesAsync();
        Assert.True(await service.CanAssistAsync(task.Id, db.Member));
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("account")]
    [InlineData("task")]
    [InlineData("project")]
    public async Task TaskAssistance_RevokedAccessPreventsHistoryAndPendingExecution(string revoke)
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        var job = await service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid());
        if (revoke == "membership") await db.Context.ProjectUsers.Where(item => item.UserId == db.Member.Id).ExecuteDeleteAsync();
        if (revoke == "account") db.Member.IsDeleted = true;
        if (revoke == "task") task.IsDeleted = true;
        if (revoke == "project") db.Project.IsDeleted = true;
        await db.Context.SaveChangesAsync();
        Assert.Null(await service.GetLatestAsync(task.Id, db.Member));
        Assert.Null(await db.Sessions.GetDetailsAsync(job.AiSessionId, db.Member));
        Assert.Empty(await db.Sessions.GetRecentAsync(db.Member));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.Execution.ExecutePendingTurnAsync(job.AiSessionId, db.Member));
        Assert.Equal(0, db.AI.CompletionCalls);
    }

    [Fact]
    public async Task TaskAssistance_DigitalTaskDisallowsNewAssistance()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        task.AssigneeType = TaskAssigneeType.DigitalEmployee;
        await db.Context.SaveChangesAsync();
        var service = new TaskAssistanceService(db.Context, db.Queue);
        Assert.False(await service.CanAssistAsync(task.Id, db.Member));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid()));
    }

    [Fact]
    public async Task TaskAssistance_RepeatedRequestReturnsSameJobAndDifferentClickCreatesNewRequest()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        var requestId = Guid.NewGuid();
        var first = await service.RequestAsync(task.Id, "next_steps", db.Member, requestId);
        var repeated = await service.RequestAsync(task.Id, "progress_update", db.Member, requestId);
        Assert.Equal(first.Id, repeated.Id);
        Assert.Contains("next_steps", repeated.AiSession!.MetadataJson);
        Assert.Single(await db.Context.AiSessions.ToListAsync());
        Assert.Single(await db.Context.AgentRunJobs.ToListAsync());
        var second = await service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid());
        Assert.NotEqual(first.Id, second.Id);
        Assert.Empty(await db.Context.BackgroundJobLeases.ToListAsync());
    }

    [Fact]
    public async Task TaskAssistance_MetadataDamageCannotEnableWrites()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        var job = await service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid());
        var session = await db.Context.AiSessions.SingleAsync();
        session.MetadataJson = "not-json";
        await db.Context.SaveChangesAsync();
        await db.Queue.ClaimNextAsync();
        await db.Queue.ProcessAsync(job.Id);
        Assert.Empty(db.AI.LastCompletionOptions!.Tools);
        Assert.Single(await db.Context.TaskComments.ToListAsync());
    }

    [Fact]
    public async Task TaskAssistance_ConcurrentScopesCannotQueueDuplicateAndCanReplayAfterCommit()
    {
        var connectionString = $"Data Source=assistance-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var db = await TestDatabase.CreateAsync(connectionString: connectionString);
        var task = await SeedAssistanceTaskAsync(db);
        await using var secondContext = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connectionString).Options);
        var secondService = CreateIndependentAssistanceService(secondContext);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        db.Events.BeforePublish = async (name, _) =>
        {
            if (name != "ai.session.queued") return;
            entered.TrySetResult();
            await release.Task;
        };
        var requestId = Guid.NewGuid();
        var firstRequest = service.RequestAsync(task.Id, "next_steps", db.Member, requestId);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<InvalidOperationException>(() => secondService.RequestAsync(task.Id, "next_steps", db.Member, requestId));
            Assert.Single(await secondContext.AgentRunJobs.ToListAsync());
            Assert.Single(await secondContext.AiSessions.ToListAsync());
        }
        finally { release.TrySetResult(); }
        var first = await firstRequest;
        var replay = await secondService.RequestAsync(task.Id, "next_steps", db.Member, requestId);
        Assert.Equal(first.Id, replay.Id);
        Assert.Empty(await secondContext.BackgroundJobLeases.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task TaskAssistance_MovingTaskCannotExposeOrExecutePreviousProjectContext()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        var job = await service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid());
        var destination = new Project { Name = "另一项目", CreatedByUserId = db.Admin.Id, LeaderUserId = db.Member.Id };
        db.Context.Add(destination);
        await db.Context.SaveChangesAsync();
        task.ProjectId = destination.Id;
        await db.Context.SaveChangesAsync();
        Assert.True(await service.CanAssistAsync(task.Id, db.Member));
        Assert.Null(await service.GetLatestAsync(task.Id, db.Member));
        Assert.Null(await db.Sessions.GetDetailsAsync(job.AiSessionId, db.Member));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.Execution.ExecutePendingTurnAsync(job.AiSessionId, db.Member));
    }

    [Fact]
    public async Task TaskAssistance_RevocationWhileModelRespondsDoesNotSavePrivateResult()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        var job = await service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid());
        db.AI.BeforeCompletion = async _ => await db.Context.ProjectUsers
            .Where(item => item.UserId == db.Member.Id).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.Execution.ExecutePendingTurnAsync(job.AiSessionId, db.Member));
        var saved = await db.Context.AiSessions.AsNoTracking().SingleAsync();
        Assert.Empty(saved.Response);
        Assert.Equal(AiSessionStatus.Failed, saved.Status);
    }

    [Fact]
    public async Task TaskAssistance_ContextReaderCannotRebindSessionAfterTaskMoves()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        var job = await service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid());
        var session = await db.Context.AiSessions.SingleAsync();
        var originalProject = session.ProjectId;
        var destination = new Project { Name = "任务迁移目标", CreatedByUserId = db.Admin.Id, LeaderUserId = db.Member.Id };
        db.Context.Add(destination);
        await db.Context.SaveChangesAsync();
        task.ProjectId = destination.Id;
        await db.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AgentContextService(db.Context, null!)
            .BuildAsync(db.Agent, session, db.Member.Id));
        Assert.Equal(originalProject, session.ProjectId);
        Assert.Empty(await db.Context.AgentToolCalls.ToListAsync());
    }

    [Fact]
    public async Task TaskAssistance_TaskMovedWhileModelRespondsDoesNotSaveResultOrRebind()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var service = new TaskAssistanceService(db.Context, db.Queue);
        var job = await service.RequestAsync(task.Id, "next_steps", db.Member, Guid.NewGuid());
        var destination = new Project { Name = "另一个有权项目", CreatedByUserId = db.Admin.Id, LeaderUserId = db.Member.Id };
        db.Context.Add(destination);
        await db.Context.SaveChangesAsync();
        db.AI.BeforeCompletion = async _ => await db.Context.ToDoTasks.Where(item => item.Id == task.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ProjectId, destination.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.Execution.ExecutePendingTurnAsync(job.AiSessionId, db.Member));
        var saved = await db.Context.AiSessions.AsNoTracking().SingleAsync();
        Assert.Equal(db.Project.Id, saved.ProjectId);
        Assert.Empty(saved.Response);
    }

    [Fact]
    public async Task TaskAssistance_RecentLimitAppliesAfterFilteringOtherUsersPrivateSessions()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var older = await db.Sessions.StartAsync("ordinary-session", "正常记录", db.Admin.Id, db.Project.Id);
        older.LastActivityAt = AppTime.Now.AddDays(-1);
        for (var i = 0; i < 9; i++)
            db.Context.AiSessions.Add(new AiSession
            {
                AgentKey = TaskAssistanceService.AgentKey, UserId = db.Member.Id, TaskId = task.Id, ProjectId = db.Project.Id,
                SessionKey = i % 2 == 0 ? $"assist:private-{i}" : $"legacy-private-{i}",
                MetadataJson = "{\"source\":\"task-assistance\",\"executionPolicy\":\"read-only\"}"
            });
        await db.Context.SaveChangesAsync();
        var recent = await db.Sessions.GetRecentAsync(db.Admin, 1);
        Assert.Equal(older.Id, Assert.Single(recent).Id);
    }

    [Fact]
    public async Task TaskAssistance_RevokedPrivateHistoryDoesNotHideOlderVisibleRecentSessions()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = await SeedAssistanceTaskAsync(db);
        var older = await db.Sessions.StartAsync("ordinary-session", "正常记录", db.Member.Id);
        older.LastActivityAt = AppTime.Now.AddDays(-1);
        var newestTime = AppTime.Now;
        for (var i = 0; i < 9; i++)
            db.Context.AiSessions.Add(new AiSession
            {
                AgentKey = TaskAssistanceService.AgentKey, UserId = db.Member.Id, TaskId = task.Id, ProjectId = db.Project.Id,
                SessionKey = $"assist:revoked-{i}", MetadataJson = "invalid-json", LastActivityAt = newestTime
            });
        await db.Context.SaveChangesAsync();
        await db.Context.ProjectUsers.Where(item => item.UserId == db.Member.Id).ExecuteDeleteAsync();
        var recent = await db.Sessions.GetRecentAsync(db.Member, 1);
        Assert.Equal(older.Id, Assert.Single(recent).Id);
    }

    private static TaskAssistanceService CreateIndependentAssistanceService(ApplicationDbContext context)
    {
        var sessions = new AiSessionService(context);
        var catalog = new AgentToolCatalog();
        var registry = new AgentRegistry(context, catalog);
        var ai = new StubAIService("测试完成");
        var events = new StubEventBus();
        var tools = new AgentToolService(context, null!, null!, sessions, events, catalog, new AgentRiskPolicyService(ai));
        var execution = new AgentExecutionService(registry, ai, sessions, events, context,
            new AgentContextService(context, null!), catalog, tools);
        var queue = new AgentRunQueueService(context, registry, sessions, execution, events, NullLogger<AgentRunQueueService>.Instance);
        return new TaskAssistanceService(context, queue);
    }

    private static async Task<ToDoTask> SeedAssistanceTaskAsync(TestDatabase db)
    {
        db.Agent.AgentKey = TaskAssistanceService.AgentKey;
        db.Agent.AutoCommentOnCompletion = true;
        // 故意扩大管理员配置，验证会话策略仍锁定当前任务而不是沿用管理员权限。
        db.Agent.ContextSourcesJson = "[\"Project\",\"ProjectTasks\",\"SelectedTask\",\"TaskComments\",\"Documents\"]";
        db.Context.ProjectUsers.Add(new ProjectUser { ProjectId = db.Project.Id, UserId = db.Member.Id });
        var task = new ToDoTask
        {
            Title = "设计成员工作台", Description = "减少成员重复录入", ProjectId = db.Project.Id,
            CreatorId = db.Admin.Id, AssigneeId = db.Member.Id, Status = TaskStatus.InProgress, Progress = 25
        };
        db.Context.Add(task);
        await db.Context.SaveChangesAsync();
        db.Context.TaskComments.Add(new TaskComment { TaskId = task.Id, AuthorId = db.Member.Id, Content = "已完成接口草稿" });
        db.Context.AgentToolPermissions.Add(new AgentToolPermission
        {
            AgentDefinitionId = db.Agent.Id, ToolName = "task.add_comment", IsEnabled = true,
            RequiresApproval = false, ReviewMode = AgentToolReviewMode.Direct
        });
        await db.Context.SaveChangesAsync();
        return task;
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;

namespace ToDo.Test;

public class AgentEventAutomationTests
{
    [Fact]
    public async Task SaveRule_RejectsAgentInternalEventToPreventTriggerLoop()
    {
        await using var database = await EventDatabase.CreateAsync();
        var rule = database.NewRule("ai.session.started");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.Subscriptions.SaveAsync(rule, database.AdminId));

        Assert.Contains("自触发循环", error.Message);
    }

    [Fact]
    public async Task SaveRule_RejectsNonAdminUser()
    {
        await using var database = await EventDatabase.CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            database.Subscriptions.SaveAsync(database.NewRule("manual.test"), database.MemberId));
    }

    [Fact]
    public async Task Dispatch_CreatesOneExecutionAndResolvesProjectScope()
    {
        await using var database = await EventDatabase.CreateAsync();
        await database.Subscriptions.SaveAsync(database.NewRule("manual.test"), database.AdminId);
        database.Context.EventBusMessages.Add(new EventBusMessage
        {
            EventType = "manual.test",
            AggregateType = "Manual",
            AggregateId = "manual",
            PayloadJson = $"{{\"projectId\":{database.ProjectId},\"reason\":\"test\"}}"
        });
        await database.Context.SaveChangesAsync();

        Assert.Equal(1, await database.Automation.DispatchPendingEventsAsync());
        Assert.Equal(0, await database.Automation.DispatchPendingEventsAsync());

        var execution = await database.Context.AgentEventExecutions.AsNoTracking().SingleAsync();
        Assert.Equal(database.ProjectId, execution.ProjectId);
        Assert.Equal(AgentEventExecutionStatus.Pending, execution.Status);
        Assert.Contains("manual.test", execution.Prompt);
        Assert.Contains(database.ProjectId.ToString(), execution.Prompt);
    }

    [Fact]
    public async Task Dispatch_ProjectRuleDoesNotMatchOtherProject()
    {
        await using var database = await EventDatabase.CreateAsync();
        var rule = database.NewRule("manual.test");
        rule.ProjectId = database.ProjectId;
        await database.Subscriptions.SaveAsync(rule, database.AdminId);
        database.Context.EventBusMessages.Add(new EventBusMessage
        {
            EventType = "manual.test",
            AggregateType = "Project",
            AggregateId = (database.ProjectId + 999).ToString(),
            PayloadJson = "{}"
        });
        await database.Context.SaveChangesAsync();

        await database.Automation.DispatchPendingEventsAsync();

        Assert.Empty(await database.Context.AgentEventExecutions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Dispatch_VisualPayloadConditionMatchesAndAuditsMismatch()
    {
        await using var database = await EventDatabase.CreateAsync();
        var rule = database.NewRule("manual.test");
        rule.ConditionField = "payload.priority";
        rule.ConditionOperator = AgentEventConditionOperator.Equals;
        rule.ConditionValue = "High";
        await database.Subscriptions.SaveAsync(rule, database.AdminId);
        database.Context.EventBusMessages.AddRange(
            new EventBusMessage { EventType = "manual.test", AggregateType = "Project", AggregateId = database.ProjectId.ToString(), PayloadJson = "{\"priority\":\"Low\"}" },
            new EventBusMessage { EventType = "manual.test", AggregateType = "Project", AggregateId = database.ProjectId.ToString(), PayloadJson = "{\"priority\":\"High\"}", CreatedAt = DateTime.Now.AddMilliseconds(1) });
        await database.Context.SaveChangesAsync();

        await database.Automation.DispatchPendingEventsAsync();

        var executions = await database.Context.AgentEventExecutions.AsNoTracking().OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(2, executions.Count);
        Assert.Equal(AgentEventExecutionStatus.Skipped, executions[0].Status);
        Assert.Contains("条件", executions[0].ErrorMessage);
        Assert.Equal(AgentEventExecutionStatus.Pending, executions[1].Status);
    }

    [Fact]
    public async Task Dispatch_DailyLimitSkipsFurtherExecution()
    {
        await using var database = await EventDatabase.CreateAsync();
        var rule = database.NewRule("manual.test");
        rule.DailyExecutionLimit = 1;
        await database.Subscriptions.SaveAsync(rule, database.AdminId);
        database.Context.EventBusMessages.AddRange(
            new EventBusMessage { EventType = "manual.test", AggregateType = "Project", AggregateId = database.ProjectId.ToString(), PayloadJson = "{}" },
            new EventBusMessage { EventType = "manual.test", AggregateType = "Project", AggregateId = database.ProjectId.ToString(), PayloadJson = "{}", CreatedAt = DateTime.Now.AddMilliseconds(1) });
        await database.Context.SaveChangesAsync();

        await database.Automation.DispatchPendingEventsAsync();

        var executions = await database.Context.AgentEventExecutions.AsNoTracking().OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(AgentEventExecutionStatus.Pending, executions[0].Status);
        Assert.Equal(AgentEventExecutionStatus.Skipped, executions[1].Status);
        Assert.Contains("每日", executions[1].ErrorMessage);
    }

    [Fact]
    public async Task Dispatch_CooldownCreatesAuditableSkippedExecution()
    {
        await using var database = await EventDatabase.CreateAsync();
        var rule = database.NewRule("manual.test");
        rule.CooldownSeconds = 3600;
        await database.Subscriptions.SaveAsync(rule, database.AdminId);
        database.Context.EventBusMessages.AddRange(
            new EventBusMessage { EventType = "manual.test", AggregateType = "Project", AggregateId = database.ProjectId.ToString(), PayloadJson = "{}" },
            new EventBusMessage { EventType = "manual.test", AggregateType = "Project", AggregateId = database.ProjectId.ToString(), PayloadJson = "{}", CreatedAt = DateTime.Now.AddMilliseconds(1) });
        await database.Context.SaveChangesAsync();

        await database.Automation.DispatchPendingEventsAsync();

        var statuses = await database.Context.AgentEventExecutions.AsNoTracking()
            .OrderBy(item => item.Id)
            .Select(item => item.Status)
            .ToListAsync();
        Assert.Equal([AgentEventExecutionStatus.Pending, AgentEventExecutionStatus.Skipped], statuses);
    }

    [Fact]
    public async Task ClaimNextExecution_UsesConditionalClaimAndCannotClaimTwice()
    {
        await using var database = await EventDatabase.CreateAsync();
        await database.Subscriptions.SaveAsync(database.NewRule("manual.test"), database.AdminId);
        database.Context.EventBusMessages.Add(new EventBusMessage
        {
            EventType = "manual.test",
            AggregateType = "Project",
            AggregateId = database.ProjectId.ToString(),
            PayloadJson = "{}"
        });
        await database.Context.SaveChangesAsync();
        await database.Automation.DispatchPendingEventsAsync();

        var first = await database.Automation.ClaimNextExecutionAsync();
        var second = await database.Automation.ClaimNextExecutionAsync();

        Assert.NotNull(first);
        Assert.Null(second);
        var execution = await database.Context.AgentEventExecutions.AsNoTracking().SingleAsync();
        Assert.Equal(AgentEventExecutionStatus.Running, execution.Status);
        Assert.Equal(1, execution.AttemptCount);
        Assert.NotNull(execution.LockedAt);
    }

    [Fact]
    public async Task RecoverStale_UsesClaimTimeInsteadOfOldEventCreationTime()
    {
        await using var database = await EventDatabase.CreateAsync();
        var active = new EventBusMessage
        {
            EventType = "manual.test",
            PayloadJson = "{}",
            Status = EventBusMessageStatus.Processing,
            CreatedAt = AppTime.Now.AddDays(-1),
            LockedAt = AppTime.Now
        };
        var stale = new EventBusMessage
        {
            EventType = "manual.test",
            PayloadJson = "{}",
            Status = EventBusMessageStatus.Processing,
            CreatedAt = AppTime.Now,
            LockedAt = AppTime.Now.AddMinutes(-20)
        };
        database.Context.EventBusMessages.AddRange(active, stale);
        await database.Context.SaveChangesAsync();

        await database.Automation.RecoverStaleAsync();

        await database.Context.Entry(active).ReloadAsync();
        await database.Context.Entry(stale).ReloadAsync();
        Assert.Equal(EventBusMessageStatus.Processing, active.Status);
        Assert.NotNull(active.LockedAt);
        Assert.Equal(EventBusMessageStatus.Pending, stale.Status);
        Assert.Null(stale.LockedAt);
    }

    [Fact]
    public async Task Dispatch_TaskRequiredAgentIsSkippedWhenEventHasNoTask()
    {
        await using var database = await EventDatabase.CreateAsync();
        var agent = await database.Context.AgentDefinitions.FirstAsync(item => item.Id == database.AgentId);
        agent.RequiresTask = true;
        await database.Context.SaveChangesAsync();
        await database.Subscriptions.SaveAsync(database.NewRule("manual.test"), database.AdminId);
        database.Context.EventBusMessages.Add(new EventBusMessage
        {
            EventType = "manual.test",
            AggregateType = "Project",
            AggregateId = database.ProjectId.ToString(),
            PayloadJson = "{}"
        });
        await database.Context.SaveChangesAsync();

        await database.Automation.DispatchPendingEventsAsync();

        var execution = await database.Context.AgentEventExecutions.AsNoTracking().SingleAsync();
        Assert.Equal(AgentEventExecutionStatus.Skipped, execution.Status);
        Assert.Contains("要求关联任务", execution.ErrorMessage);
    }

    [Fact]
    public async Task ProcessExecution_RunsAgentAndClosesSession()
    {
        await using var database = await EventDatabase.CreateAsync();
        var agent = await database.Context.AgentDefinitions.FirstAsync(item => item.Id == database.AgentId);
        agent.ContextSourcesJson = "[\"Project\"]";
        await database.Context.SaveChangesAsync();
        await database.Subscriptions.SaveAsync(database.NewRule("manual.test"), database.AdminId);
        database.Context.EventBusMessages.Add(new EventBusMessage
        {
            EventType = "manual.test",
            AggregateType = "Project",
            AggregateId = database.ProjectId.ToString(),
            PayloadJson = "{\"reason\":\"integration-test\"}"
        });
        await database.Context.SaveChangesAsync();

        await database.Automation.DispatchPendingEventsAsync();
        var executionId = await database.Automation.ClaimNextExecutionAsync();

        Assert.NotNull(executionId);
        await database.Automation.ProcessExecutionAsync(executionId!.Value);

        var execution = await database.Context.AgentEventExecutions.AsNoTracking().SingleAsync();
        Assert.Equal(AgentEventExecutionStatus.Completed, execution.Status);
        Assert.Equal("自动事件处理完成", execution.ResultSummary);
        Assert.NotNull(execution.AiSessionId);
        var session = await database.Context.AiSessions.AsNoTracking().SingleAsync();
        Assert.Equal(AiSessionStatus.Succeeded, session.Status);
        Assert.Equal(1, session.TurnCount);
        Assert.Equal(120, execution.ConsumedTokens);
        Assert.Contains(await database.Context.AgentToolCalls.AsNoTracking().ToListAsync(),
            call => call.ToolName == AgentContextService.ContextReadToolName);
    }

    private sealed class EventDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private EventDatabase(
            SqliteConnection connection,
            ApplicationDbContext context,
            AgentEventSubscriptionService subscriptions,
            AgentEventAutomationService automation,
            int adminId,
            int memberId,
            int projectId,
            int agentId)
        {
            _connection = connection;
            Context = context;
            Subscriptions = subscriptions;
            Automation = automation;
            AdminId = adminId;
            MemberId = memberId;
            ProjectId = projectId;
            AgentId = agentId;
        }

        public ApplicationDbContext Context { get; }
        public AgentEventSubscriptionService Subscriptions { get; }
        public AgentEventAutomationService Automation { get; }
        public int AdminId { get; }
        public int MemberId { get; }
        public int ProjectId { get; }
        public int AgentId { get; }

        public static async Task<EventDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var context = new ApplicationDbContext(options);
            await context.Database.EnsureCreatedAsync();
            var admin = new ApplicationUser
            {
                UserName = "event-admin",
                NormalizedUserName = "EVENT-ADMIN",
                RealName = "事件管理员",
                Role = UserRole.systemAdmin,
                Status = UserStatus.Active
            };
            var member = new ApplicationUser
            {
                UserName = "event-member",
                NormalizedUserName = "EVENT-MEMBER",
                RealName = "普通成员",
                Role = UserRole.teamMember,
                Status = UserStatus.Active
            };
            context.Users.AddRange(admin, member);
            await context.SaveChangesAsync();
            var project = new Project
            {
                Name = "事件自动化测试项目",
                Description = "测试",
                Requirements = "测试",
                CreatedByUserId = admin.Id,
                LeaderUserId = admin.Id
            };
            var agent = new AgentDefinition
            {
                AgentKey = "event-test-agent",
                Name = "事件测试 Agent",
                Description = "测试",
                SystemPrompt = "测试",
                RequiresProject = true,
                IsEnabled = true
            };
            context.AddRange(project, agent);
            await context.SaveChangesAsync();
            var subscriptions = new AgentEventSubscriptionService(context);
            var eventBus = new TestEventBus();
            var sessions = new AiSessionService(context);
            var toolCatalog = new AgentToolCatalog();
            var tools = new AgentToolService(
                context,
                null!,
                null!,
                sessions,
                eventBus,
                toolCatalog,
                new AgentRiskPolicyService(new StubAIService()));
            var execution = new AgentExecutionService(
                new AgentRegistry(context, toolCatalog),
                new StubAIService(),
                sessions,
                eventBus,
                context,
                new AgentContextService(context, null!),
                toolCatalog,
                tools);
            var automation = new AgentEventAutomationService(
                context,
                execution,
                sessions,
                NullLogger<AgentEventAutomationService>.Instance);
            return new EventDatabase(connection, context, subscriptions, automation, admin.Id, member.Id, project.Id, agent.Id);
        }

        public AgentEventSubscription NewRule(string eventType) => new()
        {
            Name = "测试订阅规则",
            EventType = eventType,
            AgentDefinitionId = AgentId,
            PromptTemplate = "处理 {eventType}，项目 {projectId}，数据 {payload}",
            MaxAttempts = 3,
            IsEnabled = true
        };

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestEventBus : IEventBus
    {
        private readonly List<EventBusMessage> _messages = [];

        public Task<EventBusMessage> PublishAsync(
            string eventType,
            object payload,
            string aggregateType = "",
            string aggregateId = "",
            CancellationToken cancellationToken = default)
        {
            var message = new EventBusMessage
            {
                EventType = eventType,
                AggregateType = aggregateType,
                AggregateId = aggregateId
            };
            _messages.Add(message);
            return Task.FromResult(message);
        }

        public Task<List<EventBusMessage>> GetRecentAsync(
            int take = 100,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_messages.Take(take).ToList());
    }

    private sealed class StubAIService : IAIService
    {
        public Task<AIMeetingMinutesResult> GenerateMeetingMinutesAsync(string transcript, string template, int projectId, CancellationToken cancellationToken = default)
    => throw new NotSupportedException();

        public Task<string> GetChatCompletionAsync(string prompt)
            => Task.FromResult("自动事件处理完成");

        public Task<string> GetChatCompletionAsync(
            string prompt,
            AIChatOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult("自动事件处理完成");

        public Task<AICompletionResult> GetChatCompletionWithUsageAsync(
            string prompt,
            AIChatOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AICompletionResult
            {
                Content = "自动事件处理完成",
                ModelName = "stub-model",
                InputTokens = 100,
                OutputTokens = 20
            }
            );

        public Task<AITaskSplitResult> SplitTaskAsync(string taskTitle, string taskDescription, string? expectedSubTaskCount = null)
            => throw new NotSupportedException();

        public Task<AIMeetingSummaryResult> ProcessMeetingMinutesAsync(string meetingContent)
            => throw new NotSupportedException();

        public Task<AIMeetingFullParseResult> ProcessMeetingMinutesFullStructAsync(string meetingContent, List<string>? memberNames = null, List<string>? projectNames = null)
            => throw new NotSupportedException();

        public Task<AIDailyReportResult> GenerateDailyReportAsync(AIDailyReportInput input)
            => throw new NotSupportedException();

        public Task<AIDailyReportResult> GenerateDailyReportByProjectAsync(DailyReportByProjectInput input)
            => throw new NotSupportedException();

        public Task<AIDailyReportResult> GenerateTeamReportAsync(TeamReportInput input)
            => throw new NotSupportedException();

        public Task<AITaskParseResult> ParseTaskTextAsync(string text, string? expectedCount = null)
            => throw new NotSupportedException();
    }
}

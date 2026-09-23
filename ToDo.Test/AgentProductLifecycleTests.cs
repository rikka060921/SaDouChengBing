using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;

namespace ToDo.Test;

public sealed class AgentProductLifecycleTests
{
    [Fact]
    public async Task BuiltInSupervisionAndAcceptanceAgents_ArePublishedReadOnlySpecialists()
    {
        await using var database = await LifecycleDatabase.CreateAsync();
        await new AgentRegistry(database.Context, new AgentToolCatalog()).EnsureDefaultsAsync();

        var agents = await database.Context.AgentDefinitions.AsNoTracking()
            .Include(item => item.ToolPermissions)
            .Include(item => item.AcceptanceContract)
            .Where(item => item.AgentKey == "meeting-supervisor" || item.AgentKey == "delivery-acceptance")
            .OrderBy(item => item.AgentKey)
            .ToListAsync();

        Assert.Equal(2, agents.Count);
        Assert.All(agents, agent =>
        {
            Assert.True(agent.IsEnabled);
            Assert.Equal(AgentLifecycleStatus.Published, agent.LifecycleStatus);
            Assert.True(agent.RequiresProject);
            Assert.True(agent.RequiresTask);
            Assert.False(agent.AutoCommentOnCompletion);
            Assert.Empty(agent.ToolPermissions);
            Assert.NotNull(agent.AcceptanceContract);
            Assert.True(agent.IsSystemManaged);
            Assert.Equal(AgentRegistry.BuiltInDefinitionVersion, agent.ManagedDefinitionVersion);
            Assert.Equal(agent.Version, agent.StableVersion);
            Assert.Contains(AgentContextSource.SelectedTask,
                AgentAdministrationService.ParseContextSources(agent.ContextSourcesJson));
        });
        Assert.True(agents.Single(item => item.AgentKey == "meeting-supervisor").CanReceiveTaskDispatch);
        Assert.False(agents.Single(item => item.AgentKey == "delivery-acceptance").CanReceiveTaskDispatch);
    }

    [Fact]
    public async Task Save_new_agent_creates_disabled_draft_and_keeps_capabilities_separate_from_tools()
    {
        await using var database = await LifecycleDatabase.CreateAsync();

        var saved = await database.SaveAgentAsync();
        var stored = await database.Context.AgentDefinitions.AsNoTracking()
            .Include(item => item.AcceptanceContract)
            .Include(item => item.ToolPermissions)
            .SingleAsync(item => item.Id == saved.Id);

        Assert.Equal(AgentLifecycleStatus.Draft, stored.LifecycleStatus);
        Assert.False(stored.IsEnabled);
        Assert.False(stored.PublicationGatePassed);
        Assert.Equal(AgentTestRunStatus.NotRun, stored.LastTestStatus);
        Assert.Equal(["project.analysis"], AgentAdministrationService.ParseCapabilities(stored.CapabilitiesJson));
        Assert.True(stored.ToolPermissions.Single(item => item.ToolName == "task.add_comment").IsEnabled);
        Assert.NotNull(stored.AcceptanceContract);
        Assert.Equal("输出可核验的项目分析", stored.AcceptanceContract!.Objective);
    }

    [Fact]
    public async Task Publish_is_blocked_until_current_version_passes_isolated_test()
    {
        await using var database = await LifecycleDatabase.CreateAsync();
        var saved = await database.SaveAgentAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.Administration.PublishAsync(saved.Id, database.Admin.Id));

        Assert.Contains("隔离测试", error.Message);
        Assert.False((await database.Context.AgentDefinitions.FindAsync(saved.Id))!.IsEnabled);
    }

    [Fact]
    public async Task Passing_test_opens_gate_without_executing_configured_business_tools()
    {
        await using var database = await LifecycleDatabase.CreateAsync();
        var saved = await database.SaveAgentAsync();

        var run = await database.Testing.RunAsync(
            saved.Id,
            database.Admin.Id,
            database.Project.Id,
            prompt: null);

        Assert.Equal(AgentTestRunStatus.Passed, run.Status);
        Assert.Empty(database.AI.LastOptions!.Tools);
        var toolCalls = await database.Context.AgentToolCalls.AsNoTracking().ToListAsync();
        Assert.All(toolCalls, call => Assert.Equal(AgentContextService.ContextReadToolName, call.ToolName));
        Assert.DoesNotContain(toolCalls, call => call.ToolName == "task.add_comment");

        await database.Administration.PublishAsync(saved.Id, database.Admin.Id);
        var published = await database.Context.AgentDefinitions.AsNoTracking().SingleAsync(item => item.Id == saved.Id);
        Assert.True(published.PublicationGatePassed);
        Assert.True(published.IsEnabled);
        Assert.Equal(AgentLifecycleStatus.Published, published.LifecycleStatus);
        Assert.NotNull(published.LastTestSessionId);
    }

    [Fact]
    public async Task Failed_validation_keeps_gate_closed_while_stable_version_serves_during_candidate_retest()
    {
        await using var database = await LifecycleDatabase.CreateAsync();
        var saved = await database.SaveAgentAsync();
        database.AI.Response = "这里没有约定的章节";

        var failed = await database.Testing.RunAsync(saved.Id, database.Admin.Id, database.Project.Id);

        Assert.Equal(AgentTestRunStatus.Failed, failed.Status);
        Assert.Contains("缺少验收关键词", failed.ValidationSummary);
        var afterFailure = await database.Context.AgentDefinitions.AsNoTracking().SingleAsync(item => item.Id == saved.Id);
        Assert.False(afterFailure.PublicationGatePassed);
        Assert.False(afterFailure.IsEnabled);

        database.AI.Response = "结论：进展正常。证据：项目目标明确。下一步：继续验证。";
        await database.Testing.RunAsync(saved.Id, database.Admin.Id, database.Project.Id);
        await database.Administration.PublishAsync(saved.Id, database.Admin.Id);

        var editedInput = LifecycleDatabase.NewInput();
        editedInput.Description = "修改后的职责";
        var edited = await database.Administration.SaveAsync(
            saved.Id,
            editedInput,
            [AgentContextSource.Project],
            ["project.analysis"],
            ["task.add_comment"],
            [],
            LifecycleDatabase.NewContract(),
            database.Admin.Id);

        Assert.Equal(2, edited.Version);
        Assert.Equal(AgentLifecycleStatus.Draft, edited.LifecycleStatus);
        Assert.True(edited.IsEnabled);
        Assert.Equal(1, edited.StableVersion);
        Assert.False(edited.PublicationGatePassed);
        Assert.Equal(AgentTestRunStatus.NotRun, edited.LastTestStatus);
    }

    [Fact]
    public async Task Canary_keeps_stable_version_routes_deterministically_and_can_be_promoted()
    {
        await using var database = await LifecycleDatabase.CreateAsync();
        var saved = await database.SaveAgentAsync();
        await database.Testing.RunAsync(saved.Id, database.Admin.Id, database.Project.Id);
        await database.Administration.PublishAsync(saved.Id, database.Admin.Id);

        var input = LifecycleDatabase.NewInput();
        input.Description = "候选版本";
        var candidate = await database.Administration.SaveAsync(
            saved.Id,
            input,
            [AgentContextSource.Project],
            ["project.analysis"],
            ["task.add_comment"],
            [],
            LifecycleDatabase.NewContract(),
            database.Admin.Id);
        await database.Testing.RunAsync(candidate.Id, database.Admin.Id, database.Project.Id);
        await database.Administration.StartCanaryAsync(candidate.Id, 10, database.Admin.Id);

        var canary = await database.Context.AgentDefinitions.AsNoTracking().SingleAsync(item => item.Id == candidate.Id);
        Assert.Equal(1, canary.StableVersion);
        Assert.Equal(2, canary.CanaryVersion);
        Assert.Equal(AgentDeploymentStatus.Canary, canary.DeploymentStatus);
        var stableRuntime = await new AgentRegistry(database.Context, new AgentToolCatalog())
            .GetForExecutionAsync(candidate.AgentKey, version: 1);
        Assert.NotNull(stableRuntime);
        Assert.Equal("测试发布流程", stableRuntime!.Description);
        Assert.Equal(1, stableRuntime.Version);
        var routes = Enumerable.Range(0, 1000)
            .Select(index => AgentRegistry.SelectDeploymentVersion(canary, $"route-{index}"))
            .ToList();
        Assert.InRange(routes.Count(version => version == 2), 60, 140);
        Assert.Equal(
            AgentRegistry.SelectDeploymentVersion(canary, "same-route"),
            AgentRegistry.SelectDeploymentVersion(canary, "same-route"));

        await database.Administration.PromoteCanaryAsync(candidate.Id, database.Admin.Id);
        var promoted = await database.Context.AgentDefinitions.AsNoTracking().SingleAsync(item => item.Id == candidate.Id);
        Assert.Equal(2, promoted.StableVersion);
        Assert.Null(promoted.CanaryVersion);
        Assert.Equal(AgentDeploymentStatus.Stable, promoted.DeploymentStatus);
    }

    [Fact]
    public async Task SaveRegistration_ActivatesWithoutTestAndPreservesHighRiskApproval()
    {
        await using var db = await LifecycleDatabase.CreateAsync();
        var input = LifecycleDatabase.NewInput();
        input.IsEnabled = true;
        var agent = await db.Administration.SaveAsync(null, input, [AgentContextSource.Project],
            ["project.update"], ["project.update"], [], LifecycleDatabase.NewContract(),
            db.Admin.Id, applyImmediately: true);
        Assert.True(agent.IsEnabled);
        Assert.Equal(agent.Version, agent.StableVersion);
        Assert.False(agent.PublicationGatePassed);
        Assert.Equal(AgentTestRunStatus.NotRun, agent.LastTestStatus);
        var permission = await db.Context.AgentToolPermissions.SingleAsync(item => item.ToolName == "project.update");
        Assert.Equal(AgentToolReviewMode.HumanApproval, permission.ReviewMode);
        Assert.True(permission.IsEnabled);
        Assert.Single(await db.Context.AgentDefinitionVersions.ToListAsync());
        Assert.NotNull(await new AgentRegistry(db.Context, new AgentToolCatalog()).GetForExecutionAsync(agent.AgentKey));
    }

    [Fact]
    public async Task EnableRegistration_DoesNotRequireModelTestOrFabricateTestSuccess()
    {
        await using var db = await LifecycleDatabase.CreateAsync();
        var agent = await db.SaveAgentAsync();
        await db.Administration.EnableAsync(agent.Id, db.Admin.Id);
        Assert.True(agent.IsEnabled);
        Assert.Equal(AgentTestRunStatus.NotRun, agent.LastTestStatus);
        Assert.False(agent.PublicationGatePassed);
        Assert.Equal(agent.Version, agent.StableVersion);
    }

    [Fact]
    public async Task OptionalTest_DoesNotReenablePausedRegistration()
    {
        await using var db = await LifecycleDatabase.CreateAsync();
        var agent = await db.SaveAgentAsync();
        await db.Administration.EnableAsync(agent.Id, db.Admin.Id);
        await db.Administration.PauseAsync(agent.Id, db.Admin.Id);
        var result = await db.Testing.RunAsync(agent.Id, db.Admin.Id, db.Project.Id);
        Assert.Equal(AgentTestRunStatus.Passed, result.Status);
        var saved = await db.Context.AgentDefinitions.AsNoTracking().SingleAsync();
        Assert.False(saved.IsEnabled);
        Assert.Equal(AgentLifecycleStatus.Paused, saved.LifecycleStatus);
    }

    [Fact]
    public async Task OptionalTest_FailureDoesNotDisableActiveRegistration()
    {
        await using var db = await LifecycleDatabase.CreateAsync();
        var agent = await db.SaveAgentAsync();
        await db.Administration.EnableAsync(agent.Id, db.Admin.Id);
        db.AI.Response = "不符合测试要求";
        var result = await db.Testing.RunAsync(agent.Id, db.Admin.Id, db.Project.Id);
        Assert.Equal(AgentTestRunStatus.Failed, result.Status);
        var saved = await db.Context.AgentDefinitions.AsNoTracking().SingleAsync();
        Assert.True(saved.IsEnabled);
        Assert.Equal(AgentLifecycleStatus.Published, saved.LifecycleStatus);
    }

    [Fact]
    public async Task SaveRegistration_UpdateKeepsPreviousRuntimeSnapshot()
    {
        await using var db = await LifecycleDatabase.CreateAsync();
        var saved = await db.SaveAgentAsync();
        await db.Administration.EnableAsync(saved.Id, db.Admin.Id);
        var input = LifecycleDatabase.NewInput();
        input.IsEnabled = true;
        input.Description = "新的职责说明";
        await db.Administration.SaveAsync(saved.Id, input, [AgentContextSource.Project],
            ["project.analysis"], ["task.add_comment"], [], LifecycleDatabase.NewContract(),
            db.Admin.Id, applyImmediately: true);
        var registry = new AgentRegistry(db.Context, new AgentToolCatalog());
        Assert.Equal("测试发布流程", (await registry.GetForExecutionAsync(saved.AgentKey, 1))!.Description);
        Assert.Equal("新的职责说明", (await registry.GetForExecutionAsync(saved.AgentKey))!.Description);
        Assert.Equal(2, await db.Context.AgentDefinitionVersions.CountAsync());
    }

    private sealed class LifecycleDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private LifecycleDatabase(
            SqliteConnection connection,
            ApplicationDbContext context,
            ApplicationUser admin,
            Project project,
            RecordingAIService ai)
        {
            _connection = connection;
            Context = context;
            Admin = admin;
            Project = project;
            AI = ai;
            var catalog = new AgentToolCatalog();
            Administration = new AgentAdministrationService(context, catalog);
            Testing = new AgentTestingService(
                context,
                new AgentContextService(context, null!),
                new AiSessionService(context),
                ai);
        }

        public ApplicationDbContext Context { get; }
        public ApplicationUser Admin { get; }
        public Project Project { get; }
        public RecordingAIService AI { get; }
        public AgentAdministrationService Administration { get; }
        public AgentTestingService Testing { get; }

        public static async Task<LifecycleDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var admin = new ApplicationUser
            {
                UserName = "lifecycle-admin",
                NormalizedUserName = "LIFECYCLE-ADMIN",
                RealName = "生命周期管理员",
                Role = UserRole.systemAdmin,
                Status = UserStatus.Active
            };
            context.Users.Add(admin);
            await context.SaveChangesAsync();
            var project = new Project
            {
                Name = "Agent 产品化测试项目",
                Description = "验证隔离测试",
                Requirements = "不产生业务写入",
                CreatedByUserId = admin.Id,
                LeaderUserId = admin.Id
            };
            context.Project.Add(project);
            await context.SaveChangesAsync();
            return new LifecycleDatabase(connection, context, admin, project, new RecordingAIService());
        }

        public Task<AgentDefinition> SaveAgentAsync() => Administration.SaveAsync(
            null,
            NewInput(),
            [AgentContextSource.Project],
            ["project.analysis"],
            ["task.add_comment"],
            [],
            NewContract(),
            Admin.Id);

        public static AgentDefinition NewInput() => new()
        {
            AgentKey = "lifecycle-agent",
            Name = "生命周期 Agent",
            Description = "测试发布流程",
            SystemPrompt = "只基于授权上下文分析，不执行写入。",
            TemplateKey = "read-only-analysis",
            Temperature = 0.2,
            MaxTokens = 1000,
            MaxTurns = 5,
            TimeoutSeconds = 30,
            RequiresProject = true,
            CanReceiveTaskDispatch = true
        };

        public static AgentAcceptanceContract NewContract() => new()
        {
            Objective = "输出可核验的项目分析",
            InputRequirements = "必须关联已授权项目",
            RequiredOutput = "结论、证据和下一步",
            SuccessCriteria = "包含约定章节且不调用工具",
            ProhibitedActions = "不得写入业务数据",
            TestPrompt = "请分析项目并输出结论、证据和下一步",
            ExpectedOutputTerms = "结论\n证据\n下一步",
            ForbiddenOutputTerms = "<agent-actions>"
        };

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingAIService : IAIService
    {
        public string Response { get; set; } = "结论：进展正常。证据：项目目标明确。下一步：继续验证。";
        public AIChatOptions? LastOptions { get; private set; }

        public Task<AICompletionResult> GetChatCompletionWithUsageAsync(string prompt, AIChatOptions options, CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new AICompletionResult
            {
                Content = Response,
                ModelName = "test-model",
                InputTokens = 120,
                OutputTokens = 30
            });
        }

        public Task<string> GetChatCompletionAsync(string prompt) => Task.FromResult(Response);
        public Task<string> GetChatCompletionAsync(string prompt, AIChatOptions options, CancellationToken cancellationToken = default) => Task.FromResult(Response);
        public Task<AITaskSplitResult> SplitTaskAsync(string taskTitle, string taskDescription, string? expectedSubTaskCount = null) => throw new NotSupportedException();
        public Task<AIMeetingSummaryResult> ProcessMeetingMinutesAsync(string meetingContent) => throw new NotSupportedException();
        public Task<AIMeetingFullParseResult> ProcessMeetingMinutesFullStructAsync(string meetingContent, List<string>? memberNames = null, List<string>? projectNames = null) => throw new NotSupportedException();
        public Task<AIDailyReportResult> GenerateDailyReportAsync(AIDailyReportInput input) => throw new NotSupportedException();
        public Task<AIDailyReportResult> GenerateDailyReportByProjectAsync(DailyReportByProjectInput input) => throw new NotSupportedException();
        public Task<AIDailyReportResult> GenerateTeamReportAsync(TeamReportInput input) => throw new NotSupportedException();
        public Task<AITaskParseResult> ParseTaskTextAsync(string text, string? expectedCount = null) => throw new NotSupportedException();
        public Task<AIMeetingMinutesResult> GenerateMeetingMinutesAsync(string transcript, string template, int projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Test;

public sealed class AgentDispatchServiceTests
{
    [Theory]
    [InlineData("draft", "")]
    [InlineData("no-read", "读取")]
    [InlineData("no-tool", "工具")]
    [InlineData("no-write", "写权限")]
    [InlineData("disabled", "停用")]
    [InlineData("composite", "独立任务")]
    public async Task Dispatch_ExplainsBusinessSpecificMissingRequirements(string scenario, string expected)
    {
        await using var db = await DispatchDatabase.CreateAsync();
        var agent = await db.AddAgentAsync("document-helper", "资料助手", ["document.read", "project.document.write"], [AgentContextSource.Documents]);
        if (scenario == "disabled") agent.IsEnabled = false;
        if (scenario != "no-read") db.Context.AgentDocumentPermissions.Add(new()
        {
            ProjectId = db.Project.Id, AgentKey = agent.AgentKey, CanRead = true,
            CanWrite = scenario != "no-write"
        });
        if (scenario == "no-write") db.Context.AgentToolPermissions.Add(new()
        {
            AgentDefinitionId = agent.Id, ToolName = "project.document.write", IsEnabled = true,
            ReviewMode = AgentToolReviewMode.HumanApproval
        });
        await db.Context.SaveChangesAsync();
        var title = scenario is "no-tool" or "no-write" ? "把概要设计保存到项目资料库"
            : scenario == "composite" ? "读取项目文档并创建任务" : "根据需求分析生成概要设计草稿，先给我看，不保存到项目资料";
        var task = await db.AddTaskAsync(title, TaskPriority.Medium);
        var result = await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id);
        if (scenario == "draft")
        {
            Assert.True(result.AssignedAutomatically);
            var work = Assert.Single(await db.Context.AgentWorkItems.ToListAsync());
            Assert.False(work.RequiresPlan);
            Assert.Empty(agent.ToolPermissions);
        }
        else
        {
            Assert.Equal(AgentDispatchDecisionStatus.NoCandidate, result.Decision.Status);
            Assert.Contains(expected, result.Decision.Explanation);
            Assert.Empty(await db.Context.AgentWorkItems.ToListAsync());
        }
    }

    [Fact]
    public void ParseCandidates_LegacyJson_DefaultsNewFeedbackFields()
    {
        var legacyJson = """
            [{"AgentDefinitionId":7,"AgentKey":"legacy","AgentName":"旧 Agent","Score":60,"CapabilityScore":20,"ProjectPermissionScore":10,"RiskScore":12,"LoadScore":10,"HistoryScore":8,"ActiveWorkItems":0,"HistoricalRuns":0,"HistoricalSuccessRate":0.5,"Reasons":[]}]
            """;

        var candidate = Assert.Single(AgentDispatchService.ParseCandidates(legacyJson));

        Assert.Equal(7, candidate.AgentDefinitionId);
        Assert.Equal(0, candidate.AcceptedDeliveries);
        Assert.Equal(0, candidate.RejectedDeliveries);
        Assert.Equal(0, candidate.FeedbackScore);
    }

    [Fact]
    public async Task DispatchTask_UniqueDutyMatch_AssignsAndEnqueuesAutomatically()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        var agent = await db.AddAgentAsync(
            "risk-reviewer",
            "任务风险审查 Agent",
            ["task.add_comment"],
            [AgentContextSource.Project, AgentContextSource.SelectedTask, AgentContextSource.TaskComments]);
        var task = await db.AddTaskAsync("高优先级任务风险审查与进度复核", TaskPriority.High);

        var result = await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id);

        Assert.True(result.AssignedAutomatically);
        Assert.Equal(AgentDispatchDecisionStatus.AutoAssigned, result.Decision.Status);
        Assert.Equal(agent.Id, result.Decision.SelectedAgentDefinitionId);
        Assert.Equal(0, result.Decision.Confidence);
        Assert.DoesNotContain("综合分", result.Decision.Explanation);
        var savedTask = await db.Context.ToDoTasks.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal(agent.Id, savedTask.AgentDefinitionId);
        Assert.Equal(AgentTaskExecutionStatus.Pending, savedTask.AgentExecutionStatus);
        Assert.Equal(AgentWorkItemStatus.Pending, (await db.Context.AgentWorkItems.SingleAsync()).Status);
    }

    [Fact]
    public async Task DispatchTask_ExplicitConfirmationRequestStillWaitsForSelection()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        var first = await db.AddAgentAsync("generic-a", "通用 Agent A", ["task.read"], []);
        var second = await db.AddAgentAsync("generic-b", "通用 Agent B", ["task.read"], []);
        var task = await db.AddTaskAsync("整理下一阶段任务", TaskPriority.Medium);

        var result = await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id, forceConfirmation: true);

        Assert.False(result.AssignedAutomatically);
        Assert.Equal(AgentDispatchDecisionStatus.PendingConfirmation, result.Decision.Status);
        Assert.Equal(AgentTaskExecutionStatus.AwaitingDispatchConfirmation,
            (await db.Context.ToDoTasks.AsNoTracking().SingleAsync(item => item.Id == task.Id)).AgentExecutionStatus);
        Assert.Empty(await db.Context.AgentWorkItems.ToListAsync());

        await db.Dispatch.ConfirmAsync(result.Decision.Id, second.Id, db.Admin.Id);

        var confirmed = await db.Context.AgentDispatchDecisions.AsNoTracking().SingleAsync();
        Assert.Equal(AgentDispatchDecisionStatus.Confirmed, confirmed.Status);
        Assert.Equal(second.Id, confirmed.SelectedAgentDefinitionId);
        Assert.Equal(second.Id, (await db.Context.ToDoTasks.AsNoTracking().SingleAsync(item => item.Id == task.Id)).AgentDefinitionId);
        Assert.Single(await db.Context.AgentWorkItems.ToListAsync());
        Assert.NotEqual(first.Id, confirmed.SelectedAgentDefinitionId);
        var dispatchSignals = await db.Context.AgentPerformanceSignals.OrderBy(item => item.Id).ToListAsync();
        Assert.Null(result.Decision.RecommendedAgentDefinitionId);
        var signal = Assert.Single(dispatchSignals);
        Assert.Equal(second.Id, signal.AgentDefinitionId);
        Assert.Equal(AgentPerformanceEventType.HumanSelected, signal.EventType);
    }

    [Fact]
    public async Task DispatchTask_MultipleMatchesSelectsLeastBusyAndEnqueuesOnce()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        var first = await db.AddAgentAsync("busy-agent", "忙碌助手", ["task.read"], []);
        var second = await db.AddAgentAsync("available-agent", "空闲助手", ["task.read"], []);
        var oldTask = await db.AddTaskAsync("已有任务", TaskPriority.Medium, first.Id);
        db.Context.AgentWorkItems.Add(new AgentWorkItem { AgentDefinitionId = first.Id, ProjectId = db.Project.Id,
            TaskId = oldTask.Id, RequestedByUserId = db.Admin.Id, IdempotencyKey = "existing-load", Status = AgentWorkItemStatus.Running });
        await db.Context.SaveChangesAsync();
        var task = await db.AddTaskAsync("整理下一阶段任务", TaskPriority.Medium);
        var result = await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id);
        Assert.True(result.AssignedAutomatically);
        Assert.Equal(second.Id, result.Decision.SelectedAgentDefinitionId);
        Assert.Contains("无需人工", result.Decision.Explanation);
        await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id);
        Assert.Single(await db.Context.AgentWorkItems.Where(w => w.TaskId == task.Id).ToListAsync());
    }

    [Fact]
    public async Task DispatchTask_EqualLoadUsesStableRegistrationOrder()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        var first = await db.AddAgentAsync("first-agent", "助手甲", ["task.read"], []);
        await db.AddAgentAsync("second-agent", "助手乙", ["task.read"], []);
        var task = await db.AddTaskAsync("整理任务进度", TaskPriority.Medium);
        var result = await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id);
        Assert.True(result.AssignedAutomatically);
        Assert.Equal(first.Id, result.Decision.SelectedAgentDefinitionId);
    }

    [Fact]
    public async Task DispatchTask_LoadAndHistoryDoNotRankDutyMatches()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        var busy = await db.AddAgentAsync("busy-agent", "任务 Agent", ["task.read"], [AgentContextSource.SelectedTask]);
        var proven = await db.AddAgentAsync("proven-agent", "任务 Agent", ["task.read"], [AgentContextSource.SelectedTask]);
        var historicalTask = await db.AddTaskAsync("历史任务", TaskPriority.Medium, proven.Id);
        db.Context.AgentWorkItems.AddRange(
            new AgentWorkItem
            {
                AgentDefinitionId = busy.Id, ProjectId = db.Project.Id, TaskId = historicalTask.Id,
                RequestedByUserId = db.Admin.Id, IdempotencyKey = "busy-1", Status = AgentWorkItemStatus.Running
            },
            new AgentWorkItem
            {
                AgentDefinitionId = proven.Id, ProjectId = db.Project.Id, TaskId = historicalTask.Id,
                RequestedByUserId = db.Admin.Id, IdempotencyKey = "proven-1", Status = AgentWorkItemStatus.Completed
            });
        await db.Context.SaveChangesAsync();
        var task = await db.AddTaskAsync("任务进度跟进", TaskPriority.Medium);

        var result = await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id, forceConfirmation: true);

        Assert.Equal(new[] { busy.Id, proven.Id }, result.Candidates.Select(item => item.AgentDefinitionId));
        Assert.Null(result.Decision.RecommendedAgentDefinitionId);
        Assert.All(result.Candidates, item => { Assert.Equal(0, item.LoadScore); Assert.Equal(0, item.HistoryScore); });
    }

    [Fact]
    public async Task DispatchTask_AcceptanceHistoryRemainsRecordedButDoesNotAffectMatching()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        var accepted = await db.AddAgentAsync("accepted-agent", "任务 Agent", ["task.read"], [AgentContextSource.SelectedTask]);
        var rejected = await db.AddAgentAsync("rejected-agent", "任务 Agent", ["task.read"], [AgentContextSource.SelectedTask]);
        db.Context.AgentPerformanceSignals.AddRange(
            new AgentPerformanceSignal
            {
                AgentDefinitionId = accepted.Id,
                ProjectId = db.Project.Id,
                EventType = AgentPerformanceEventType.HumanAccepted,
                ScoreDelta = 8,
                EventKey = "dispatch-history-accepted",
                Reason = "人工验收通过"
            },
            new AgentPerformanceSignal
            {
                AgentDefinitionId = rejected.Id,
                ProjectId = db.Project.Id,
                EventType = AgentPerformanceEventType.HumanRejected,
                ScoreDelta = -10,
                EventKey = "dispatch-history-rejected",
                Reason = "人工验收驳回"
            });
        await db.Context.SaveChangesAsync();
        var task = await db.AddTaskAsync("任务进度跟进", TaskPriority.Medium);

        var result = await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id, forceConfirmation: true);

        Assert.Equal(new[] { accepted.Id, rejected.Id }, result.Candidates.Select(item => item.AgentDefinitionId));
        Assert.Null(result.Decision.RecommendedAgentDefinitionId);
        Assert.All(result.Candidates, item => Assert.Equal(0, item.HistoryScore));
        Assert.Equal(2, await db.Context.AgentPerformanceSignals.CountAsync());
    }

    [Fact]
    public async Task DispatchTask_UnknownPurpose_DoesNotForceGenericAgent()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        await db.AddAgentAsync("generic", "通用员工", ["task.read"], []);
        var task = await db.AddTaskAsync("帮我买一杯咖啡", TaskPriority.High);
        var result = await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id);
        Assert.Equal(AgentDispatchDecisionStatus.NoCandidate, result.Decision.Status);
        Assert.Empty(result.Candidates);
        Assert.Empty(await db.Context.AgentWorkItems.ToListAsync());
        Assert.Contains("说明希望得到的成果", result.Decision.Explanation);
    }

    [Fact]
    public async Task DispatchTask_UnrelatedEnabledAgentIsNotACandidate()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        var report = await db.AddAgentAsync("report", "报告助手", ["daily-report"], []);
        await db.AddAgentAsync("meeting", "会议助手", ["meeting-summary"], []);
        var task = await db.AddTaskAsync("整理项目周报", TaskPriority.Medium);
        var result = await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id);
        Assert.Equal(report.Id, Assert.Single(result.Candidates).AgentDefinitionId);
        Assert.True(result.AssignedAutomatically);
    }

    [Fact]
    public async Task DispatchTask_DocumentReaderRequiresCurrentProjectGrant()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        var reader = await db.AddAgentAsync("reader", "资料助手", ["document.read"], [AgentContextSource.Documents]);
        var task = await db.AddTaskAsync("总结项目资料", TaskPriority.Medium);
        var result = await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id);
        Assert.Empty(result.Candidates);
        Assert.Empty(await db.Context.AgentWorkItems.ToListAsync());
        db.Context.AgentDocumentPermissions.Add(new AgentDocumentPermission
        {
            ProjectId = db.Project.Id, AgentKey = reader.AgentKey, CanRead = true
        });
        await db.Context.SaveChangesAsync();
        var another = await db.AddTaskAsync("总结项目资料正文", TaskPriority.Medium);
        var permitted = await db.Dispatch.DispatchTaskAsync(another.Id, db.Admin.Id);
        Assert.Equal(reader.Id, Assert.Single(permitted.Candidates).AgentDefinitionId);
        Assert.True(permitted.AssignedAutomatically);
    }

    [Fact]
    public async Task DispatchTask_WriteCapabilityDoesNotGrantToolOrDocumentPermission()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        var writer = await db.AddAgentAsync("writer", "文档助手", ["project.document.write"], []);
        var first = await db.AddTaskAsync("生成概要设计文档并保存到项目资料库", TaskPriority.Medium);
        Assert.Empty((await db.Dispatch.DispatchTaskAsync(first.Id, db.Admin.Id)).Candidates);
        db.Context.AgentToolPermissions.Add(new AgentToolPermission
        {
            AgentDefinitionId = writer.Id, ToolName = "project.document.write", IsEnabled = true,
            ReviewMode = AgentToolReviewMode.HumanApproval
        });
        await db.Context.SaveChangesAsync();
        var second = await db.AddTaskAsync("生成概要设计文档并保存到项目资料库", TaskPriority.Medium);
        Assert.Empty((await db.Dispatch.DispatchTaskAsync(second.Id, db.Admin.Id)).Candidates);
        db.Context.AgentDocumentPermissions.Add(new AgentDocumentPermission
        {
            ProjectId = db.Project.Id, AgentKey = writer.AgentKey, CanRead = true, CanWrite = true
        });
        await db.Context.SaveChangesAsync();
        var third = await db.AddTaskAsync("生成概要设计文档并保存到项目资料库", TaskPriority.Medium);
        Assert.True((await db.Dispatch.DispatchTaskAsync(third.Id, db.Admin.Id)).AssignedAutomatically);
        Assert.Equal(AgentToolReviewMode.HumanApproval, (await db.Context.AgentToolPermissions.SingleAsync()).ReviewMode);
    }

    [Fact]
    public async Task ConfirmDispatch_RechecksRevokedProjectGrant()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        var reader = await db.AddAgentAsync("reader", "资料助手", ["document.read"], [AgentContextSource.Documents]);
        var grant = new AgentDocumentPermission { ProjectId = db.Project.Id, AgentKey = reader.AgentKey, CanRead = true };
        db.Context.AgentDocumentPermissions.Add(grant);
        await db.Context.SaveChangesAsync();
        var task = await db.AddTaskAsync("总结项目资料", TaskPriority.Medium);
        var result = await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id, forceConfirmation: true);
        Assert.Single(result.Candidates);
        grant.CanRead = false;
        await db.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Dispatch.ConfirmAsync(result.Decision.Id, reader.Id, db.Admin.Id));
        Assert.Empty(await db.Context.AgentWorkItems.ToListAsync());
    }

    [Fact]
    public async Task ConfirmDispatch_RechecksChangedCapabilities()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        var agent = await db.AddAgentAsync("report", "报告助手", ["daily-report"], []);
        var task = await db.AddTaskAsync("整理日报", TaskPriority.Medium);
        var result = await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id, forceConfirmation: true);
        agent.CapabilitiesJson = "[\"meeting-summary\"]";
        await db.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Dispatch.ConfirmAsync(result.Decision.Id, agent.Id, db.Admin.Id));
        Assert.Empty(await db.Context.AgentWorkItems.ToListAsync());
    }

    [Fact]
    public async Task DispatchTask_RepeatRequestDoesNotDuplicateDecisionOrWork()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        await db.AddAgentAsync("report", "日报助手", ["daily-report"], []);
        var task = await db.AddTaskAsync("生成日报", TaskPriority.Medium);
        var first = await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id);
        var second = await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id);
        Assert.Equal(first.Decision.Id, second.Decision.Id);
        Assert.Single(await db.Context.AgentWorkItems.ToListAsync());
    }

    [Fact]
    public async Task DispatchTask_MultipleOperationsRequireAllCapabilities()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        var partial = await db.AddAgentAsync("partial", "任务修改助手", ["task.update"], []);
        db.Context.AgentToolPermissions.Add(new AgentToolPermission
        {
            AgentDefinitionId = partial.Id, ToolName = "task.update", IsEnabled = true,
            ReviewMode = AgentToolReviewMode.HumanApproval
        });
        await db.Context.SaveChangesAsync();
        var task = await db.AddTaskAsync("修改任务并生成周报", TaskPriority.Medium);
        Assert.Empty((await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id)).Candidates);
    }

    [Fact]
    public async Task DispatchTask_DepartedCreatorCannotDispatchOrCreateDecision()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        await db.AddAgentAsync("reader", "进度助手", ["task.read"], []);
        var creator = await db.AddMemberAsync();
        var task = await db.AddTaskAsync("整理任务进度", TaskPriority.Medium);
        task.CreatorId = creator.Id;
        db.Context.ProjectUsers.Remove(await db.Context.ProjectUsers.SingleAsync());
        await db.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.Dispatch.DispatchTaskAsync(task.Id, creator.Id));

        Assert.Empty(await db.Context.AgentDispatchDecisions.ToListAsync());
        Assert.Empty(await db.Context.AgentWorkItems.ToListAsync());
        Assert.Null(task.AgentDefinitionId);
    }

    [Theory]
    [InlineData(true, (int)ProjectRole.Member)]
    [InlineData(false, (int)ProjectRole.Admin)]
    public async Task DispatchTask_CurrentCreatorOrProjectAdminCanStillDispatch(bool isCreator, int projectRole)
    {
        await using var db = await DispatchDatabase.CreateAsync();
        await db.AddAgentAsync("reader", "进度助手", ["task.read"], []);
        var member = await db.AddMemberAsync(projectRole);
        var task = await db.AddTaskAsync("整理任务进度", TaskPriority.Medium);
        if (isCreator) task.CreatorId = member.Id;
        await db.Context.SaveChangesAsync();

        var result = await db.Dispatch.DispatchTaskAsync(task.Id, member.Id);

        Assert.True(result.AssignedAutomatically);
        Assert.Equal(member.Id, (await db.Context.AgentWorkItems.SingleAsync()).RequestedByUserId);
    }

    [Fact]
    public async Task DispatchTask_DeletedProjectCannotDispatchEvenForSystemAdmin()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        await db.AddAgentAsync("reader", "进度助手", ["task.read"], []);
        var task = await db.AddTaskAsync("整理任务进度", TaskPriority.Medium);
        db.Project.IsDeleted = true;
        await db.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id));

        Assert.Empty(await db.Context.AgentDispatchDecisions.ToListAsync());
        Assert.Empty(await db.Context.AgentWorkItems.ToListAsync());
    }

    [Fact]
    public async Task ConfirmDispatch_RechecksCreatorMembershipBeforeAssignment()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        var agent = await db.AddAgentAsync("reader", "进度助手", ["task.read"], []);
        var creator = await db.AddMemberAsync();
        var task = await db.AddTaskAsync("整理任务进度", TaskPriority.Medium);
        task.CreatorId = creator.Id;
        await db.Context.SaveChangesAsync();
        var result = await db.Dispatch.DispatchTaskAsync(task.Id, creator.Id, forceConfirmation: true);
        db.Context.ProjectUsers.Remove(await db.Context.ProjectUsers.SingleAsync());
        await db.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            db.Dispatch.ConfirmAsync(result.Decision.Id, agent.Id, creator.Id));

        Assert.Equal(AgentDispatchDecisionStatus.PendingConfirmation,
            (await db.Context.AgentDispatchDecisions.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await db.Context.AgentWorkItems.ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingDispatches_InvalidCreatorsCannotBlockLaterTasksOrBorrowAdminRights(bool deletedUser)
    {
        await using var db = await DispatchDatabase.CreateAsync();
        await db.AddAgentAsync("reader", "进度助手", ["task.read"], []);
        var formerMember = await db.AddMemberAsync();
        if (deletedUser) formerMember.IsDeleted = true;
        else db.Context.ProjectUsers.Remove(await db.Context.ProjectUsers.SingleAsync());
        await db.Context.SaveChangesAsync();
        // 超过一个维护批次，确保过滤发生在 Take(10) 之前，不只是跳过首批后继续卡住。
        for (var i = 0; i < 12; i++)
        {
            var blocked = await db.AddTaskAsync($"失效发起人的任务进度 {i}", TaskPriority.Medium);
            blocked.CreatorId = formerMember.Id;
            blocked.UpdatedAt = AppTime.Now.AddDays(-1);
        }
        await db.Context.SaveChangesAsync();
        var valid = await db.AddTaskAsync("整理当前任务进度", TaskPriority.Medium);

        await db.Dispatch.EnsurePendingDispatchesAsync();
        await db.Dispatch.EnsurePendingDispatchesAsync();

        var decision = Assert.Single(await db.Context.AgentDispatchDecisions.ToListAsync());
        Assert.Equal(valid.Id, decision.TaskId);
        var work = Assert.Single(await db.Context.AgentWorkItems.ToListAsync());
        Assert.Equal(valid.Id, work.TaskId);
        Assert.Equal(db.Admin.Id, work.RequestedByUserId);
        Assert.All(await db.Context.ToDoTasks.Where(t => t.CreatorId == formerMember.Id).ToListAsync(),
            task => Assert.Null(task.AgentDefinitionId));
    }

    [Fact]
    public async Task PendingDispatches_PreservesExplicitHumanConfirmation()
    {
        await using var db = await DispatchDatabase.CreateAsync();
        await db.AddAgentAsync("reader", "进度助手", ["task.read"], []);
        var task = await db.AddTaskAsync("整理任务进度", TaskPriority.Medium);
        var result = await db.Dispatch.DispatchTaskAsync(task.Id, db.Admin.Id, forceConfirmation: true);

        await db.Dispatch.EnsurePendingDispatchesAsync();

        Assert.Equal(AgentDispatchDecisionStatus.PendingConfirmation,
            (await db.Context.AgentDispatchDecisions.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await db.Context.AgentWorkItems.ToListAsync());
    }

    private sealed class DispatchDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private DispatchDatabase(SqliteConnection connection, ApplicationDbContext context, ApplicationUser admin, Project project)
        {
            _connection = connection;
            Context = context;
            Admin = admin;
            Project = project;
            var eventBus = new StubEventBus();
            var queue = new AgentWorkQueueService(
                context,
                null!,
                null!,
                new UserNotificationService(context),
                eventBus,
                new AgentOutcomeService(context),
                NullLogger<AgentWorkQueueService>.Instance);
            Dispatch = new AgentDispatchService(context, queue, eventBus, new AgentOutcomeService(context));
        }

        public ApplicationDbContext Context { get; }
        public ApplicationUser Admin { get; }
        public Project Project { get; }
        public AgentDispatchService Dispatch { get; }

        public async Task<ApplicationUser> AddMemberAsync(int projectRole = (int)ProjectRole.Member)
        {
            var member = new ApplicationUser
            {
                UserName = "dispatch-member", Role = UserRole.teamMember, Status = UserStatus.Active
            };
            Context.Users.Add(member);
            await Context.SaveChangesAsync();
            Context.ProjectUsers.Add(new ProjectUser { ProjectId = Project.Id, UserId = member.Id, ProjectRole = projectRole });
            await Context.SaveChangesAsync();
            return member;
        }

        public static async Task<DispatchDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var admin = new ApplicationUser
            {
                UserName = "dispatch-admin",
                NormalizedUserName = "DISPATCH-ADMIN",
                RealName = "调度管理员",
                Role = UserRole.systemAdmin,
                Status = UserStatus.Active
            };
            context.Users.Add(admin);
            await context.SaveChangesAsync();
            var project = new Project
            {
                Name = "调度测试项目",
                CreatedByUserId = admin.Id,
                LeaderUserId = admin.Id
            };
            context.Project.Add(project);
            await context.SaveChangesAsync();
            return new DispatchDatabase(connection, context, admin, project);
        }

        public async Task<AgentDefinition> AddAgentAsync(
            string key,
            string name,
            IReadOnlyCollection<string> capabilities,
            IReadOnlyCollection<AgentContextSource> contexts)
        {
            var agent = new AgentDefinition
            {
                AgentKey = key,
                Name = name,
                Description = name,
                SystemPrompt = "测试",
                IsEnabled = true,
                CanReceiveTaskDispatch = true,
                CapabilitiesJson = JsonSerializer.Serialize(capabilities),
                ContextSourcesJson = JsonSerializer.Serialize(contexts.Select(item => item.ToString()))
            };
            Context.AgentDefinitions.Add(agent);
            await Context.SaveChangesAsync();
            return agent;
        }

        public async Task<ToDoTask> AddTaskAsync(string title, TaskPriority priority, int? assignedAgentId = null)
        {
            var task = new ToDoTask
            {
                Title = title,
                CreatorId = Admin.Id,
                ProjectId = Project.Id,
                Priority = priority,
                AssigneeType = TaskAssigneeType.DigitalEmployee,
                AgentDefinitionId = assignedAgentId,
                AgentAssignmentVersion = 1,
                AgentExecutionStatus = assignedAgentId.HasValue
                    ? AgentTaskExecutionStatus.Completed
                    : AgentTaskExecutionStatus.AwaitingDispatchConfirmation
            };
            Context.ToDoTasks.Add(task);
            await Context.SaveChangesAsync();
            return task;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class StubEventBus : IEventBus
    {
        public Task<EventBusMessage> PublishAsync(string eventType, object payload, string aggregateType = "", string aggregateId = "", CancellationToken cancellationToken = default)
            => Task.FromResult(new EventBusMessage { EventType = eventType, AggregateType = aggregateType, AggregateId = aggregateId });

        public Task<List<EventBusMessage>> GetRecentAsync(int take = 100, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<EventBusMessage>());
    }
}

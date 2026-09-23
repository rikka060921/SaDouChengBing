using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;
using TaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Test;

public sealed partial class AgentFrameworkCompletionTests
{
    private const string GoodAutomaticReview = """
        {"routine":true,"requirementsClear":true,"complete":true,"grounded":true,
        "checks":[{"criterion":"列出当前项目名称和进展依据","evidence":"名称：Agent 测试项目","passed":true}],
        "missing":[],"risks":[],"reason":"交付覆盖要求，项目名称与实际项目记录一致"}
        """;
    private const string AnalysisOutput = "成果：当前项目为 Agent 测试项目。依据：项目基本信息中的名称及项目任务列表。当前只有本项进度整理任务正在执行，不能将正在处理的工作计作已完成。未解决的问题：没有额外的进度记录可供比较。";

    private static async Task<AgentWorkItem> OrdinaryWorkAsync(TestDatabase db, string? description = null)
    {
        db.Agent.ContextSourcesJson = JsonSerializer.Serialize(new[] { "Project", "ProjectTasks" });
        db.AI.SetCompletion(new AICompletionResult { Content = AnalysisOutput, ModelName = "stub", InputTokens = 100, OutputTokens = 30 });
        db.AI.AutomaticReviewResponse = GoodAutomaticReview;
        var task = new ToDoTask
        {
            Title = "整理任务进度", Description = description ?? "只读分析，列出当前项目名称、进展和依据，不写回。",
            CreatorId = db.Admin.Id, ReviewerId = db.Admin.Id, ProjectId = db.Project.Id,
            AssigneeType = TaskAssigneeType.DigitalEmployee, AgentDefinitionId = db.Agent.Id, AgentAssignmentVersion = 1
        };
        db.Context.Add(task);
        await db.Context.SaveChangesAsync();
        var work = (await db.AssignmentQueue.EnqueueTaskAsync(task.Id, db.Admin.Id, AgentWorkTriggerType.TaskAssigned,
            task.Id, AgentWorkQueueService.BuildAssignmentPrompt(task), $"ordinary:{task.Id}"))!;
        if (work.RequiresPlan)
        {
            Assert.Equal(work.Id, await db.AssignmentQueue.ClaimNextAsync());
            await db.AssignmentQueue.ProcessAsync(work.Id);
        }
        return work;
    }

    private static async Task ExecuteOrdinaryAsync(TestDatabase db, AgentWorkItem work)
    {
        Assert.Equal(work.Id, await db.AssignmentQueue.ClaimNextAsync());
        await db.AssignmentQueue.ProcessAsync(work.Id);
    }

    [Fact]
    public async Task OrdinaryTask_ExecutesAndAcceptsWithoutPlanOrHuman()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db);
        var planned = await db.Context.AgentWorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(AgentWorkItemStatus.Pending, planned.Status);
        Assert.False(planned.RequiresPlan);
        Assert.Null(planned.PlanApprovedAt);
        Assert.Null(planned.PlanApprovedByUserId);
        Assert.Null(planned.PlanningSessionId);
        Assert.Empty(await db.Context.AgentDeliveryReceipts.ToListAsync());
        await ExecuteOrdinaryAsync(db, work);
        var task = await db.Context.ToDoTasks.AsNoTracking().SingleAsync();
        Assert.Equal(TaskStatus.Completed, task.Status);
        Assert.True(task.IsCompleted);
        Assert.Equal(100, task.Progress);
        Assert.Equal(AgentTaskExecutionStatus.Completed, task.AgentExecutionStatus);
        var receipt = await db.Context.AgentDeliveryReceipts.SingleAsync();
        Assert.Equal(AgentDeliveryAcceptanceStatus.AutomaticallyAccepted, receipt.AcceptanceStatus);
        Assert.Null(receipt.ReviewedByUserId);
        Assert.Contains(AgentAutomaticAcceptanceService.Policy, receipt.ReviewComment);
        Assert.True(AgentOutcomeService.VerifyContentHash(receipt));
        Assert.Empty(await db.Context.UserNotifications.ToListAsync());
        Assert.Empty(db.AI.AutomaticReviewOptions!.Tools);
        await db.AssignmentQueue.ProcessAsync(work.Id);
        Assert.Single(await db.Context.AgentDeliveryReceipts.ToListAsync());
        Assert.Equal(1, db.AI.AutomaticReviewCalls);
        Assert.Equal(1, db.AI.CompletionCalls);
        Assert.Null((await db.Context.AgentWorkItems.AsNoTracking().SingleAsync()).PlanningSessionId);
        Assert.Equal(1, await db.Context.AgentPerformanceSignals.CountAsync(s => s.EventType == AgentPerformanceEventType.AutomaticallyAccepted));
        Assert.False(await db.Context.AgentPerformanceSignals.AnyAsync(s => s.EventType == AgentPerformanceEventType.HumanAccepted));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("模型说完成了")]
    [InlineData("```json\n{}\n```")]
    [InlineData("{\"routine\":true}")]
    public async Task OrdinaryTask_InvalidReviewRetainsDeliveryForHuman(string review)
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db);
        db.AI.AutomaticReviewResponse = review;
        await ExecuteOrdinaryAsync(db, work);
        Assert.Equal(TaskStatus.PendingConfirmation, (await db.Context.ToDoTasks.AsNoTracking().SingleAsync()).Status);
        var receipt = await db.Context.AgentDeliveryReceipts.SingleAsync();
        Assert.Equal(AgentDeliveryAcceptanceStatus.PendingReview, receipt.AcceptanceStatus);
        Assert.NotEmpty(receipt.ReviewComment);
        Assert.Equal(AgentWorkItemStatus.Completed, (await db.Context.AgentWorkItems.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("false-check")]
    [InlineData("no-checks")]
    [InlineData("fabricated-evidence")]
    [InlineData("missing-work")]
    [InlineData("important-change")]
    public void AutomaticReview_DoesNotAcceptIncompleteOrInventedChecks(string kind)
    {
        var json = GoodAutomaticReview;
        if (kind == "false-check") json = json.Replace("\"passed\":true", "\"passed\":false");
        if (kind == "no-checks") json = json.Replace("[{\"criterion\":\"列出当前项目名称和进展依据\",\"evidence\":\"名称：Agent 测试项目\",\"passed\":true}]", "[]");
        if (kind == "fabricated-evidence") json = json.Replace("名称：Agent 测试项目", "并不存在的已完成记录");
        if (kind == "missing-work") json = json.Replace("\"missing\":[]", "\"missing\":[\"缺少产物\"]");
        if (kind == "important-change") json = json.Replace("\"routine\":true", "\"routine\":false");
        Assert.False(AgentAutomaticAcceptanceService.ParseReview(json, "名称：Agent 测试项目").Accepted);
    }

    [Theory]
    [InlineData("人工验收")]
    [InlineData("覆盖项目资料")]
    [InlineData("生成报告并保存到系统")]
    public async Task OrdinaryTask_ExplicitHumanOrUnprovedWritesCannotAutoComplete(string request)
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db, $"整理项目任务进度。{request}");
        await ExecuteOrdinaryAsync(db, work);
        Assert.Equal(TaskStatus.PendingConfirmation, (await db.Context.ToDoTasks.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, db.AI.AutomaticReviewCalls);
    }

    [Fact]
    public async Task OrdinaryTask_ReviewOutageDoesNotReplayBusinessExecution()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db);
        db.AI.BeforeAutomaticReview = () => throw new HttpRequestException("review unavailable");
        await ExecuteOrdinaryAsync(db, work);
        Assert.Equal(AgentWorkItemStatus.Completed, (await db.Context.AgentWorkItems.AsNoTracking().SingleAsync()).Status);
        Assert.Contains("暂不可用", (await db.Context.AgentDeliveryReceipts.SingleAsync()).ReviewComment);
        Assert.Null(await db.AssignmentQueue.ClaimNextAsync());
    }

    [Fact]
    public async Task OrdinaryTask_ChangedDuringReviewCannotCompleteNewRequirements()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db);
        db.AI.BeforeAutomaticReview = async () => await db.Context.ToDoTasks.Where(t => t.Id == work.TaskId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Description, "新工作要求")
                .SetProperty(t => t.ConcurrencyVersion, t => t.ConcurrencyVersion + 1));
        await ExecuteOrdinaryAsync(db, work);
        var task = await db.Context.ToDoTasks.AsNoTracking().SingleAsync();
        Assert.NotEqual(TaskStatus.Completed, task.Status);
        Assert.Equal("新工作要求", task.Description);
        Assert.Empty(await db.Context.AgentDeliveryReceipts.ToListAsync());
        Assert.Equal(AgentWorkItemStatus.Cancelled, (await db.Context.AgentWorkItems.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task OrdinaryTask_RevokedAccessDuringReviewCannotAutoComplete()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db);
        db.AI.BeforeAutomaticReview = async () => await db.Context.Users.Where(u => u.Id == db.Admin.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeleted, true));
        await ExecuteOrdinaryAsync(db, work);
        Assert.Equal(TaskStatus.PendingConfirmation, (await db.Context.ToDoTasks.AsNoTracking().SingleAsync()).Status);
        Assert.Contains("权限", (await db.Context.AgentDeliveryReceipts.SingleAsync()).ReviewComment);
    }

    [Fact]
    public async Task OrdinaryTask_UnfinishedChildPreventsAutomaticCompletion()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db);
        db.Context.Add(new ToDoTask { Title = "未完成子任务", CreatorId = db.Admin.Id, ProjectId = db.Project.Id, ParentTaskId = work.TaskId });
        await db.Context.SaveChangesAsync();
        await ExecuteOrdinaryAsync(db, work);
        Assert.Contains("子任务", (await db.Context.AgentDeliveryReceipts.SingleAsync()).ReviewComment);
        Assert.Equal(0, db.AI.AutomaticReviewCalls);
    }

    [Fact]
    public async Task OrdinaryTask_ChildAddedDuringReviewPreventsAutomaticCompletion()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db);
        // 模拟不读取任务列表的会议 / 调研 Agent，不能依赖上下文变化间接发现新增子任务。
        db.Agent.ContextSourcesJson = JsonSerializer.Serialize(new[] { "Project" });
        await db.Context.SaveChangesAsync();
        db.AI.BeforeAutomaticReview = async () =>
        {
            await using var concurrent = new ToDo.Context.ApplicationDbContext(
                new DbContextOptionsBuilder<ToDo.Context.ApplicationDbContext>()
                    .UseSqlite(db.Context.Database.GetDbConnection()).Options);
            concurrent.Add(new ToDoTask
            {
                Title = "复核期间新增的子任务", CreatorId = db.Admin.Id,
                ProjectId = db.Project.Id, ParentTaskId = work.TaskId
            });
            await concurrent.SaveChangesAsync();
        };

        await ExecuteOrdinaryAsync(db, work);

        var task = await db.Context.ToDoTasks.AsNoTracking().SingleAsync(t => t.Id == work.TaskId);
        Assert.Equal(TaskStatus.PendingConfirmation, task.Status);
        var receipt = await db.Context.AgentDeliveryReceipts.SingleAsync();
        Assert.Equal(AgentDeliveryAcceptanceStatus.PendingReview, receipt.AcceptanceStatus);
        Assert.Contains("子任务", receipt.ReviewComment);
        Assert.Equal(1, db.AI.AutomaticReviewCalls);
        Assert.Equal(AgentWorkItemStatus.Completed,
            (await db.Context.AgentWorkItems.AsNoTracking().SingleAsync(w => w.Id == work.Id)).Status);
    }

    [Fact]
    public async Task OrdinaryTask_WorkAddedDuringReviewPreventsAutomaticCompletion()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db);
        db.Agent.ContextSourcesJson = JsonSerializer.Serialize(new[] { "Project" });
        await db.Context.SaveChangesAsync();
        db.AI.BeforeAutomaticReview = async () =>
        {
            await using var concurrent = new ToDo.Context.ApplicationDbContext(
                new DbContextOptionsBuilder<ToDo.Context.ApplicationDbContext>()
                    .UseSqlite(db.Context.Database.GetDbConnection()).Options);
            concurrent.Add(new AgentWorkItem
            {
                AgentDefinitionId = db.Agent.Id, TaskId = work.TaskId, ProjectId = db.Project.Id,
                RequestedByUserId = db.Admin.Id, Status = AgentWorkItemStatus.Pending,
                IdempotencyKey = "work-added-during-automatic-review", Prompt = "补充工作要求"
            });
            await concurrent.SaveChangesAsync();
        };

        await ExecuteOrdinaryAsync(db, work);

        var task = await db.Context.ToDoTasks.AsNoTracking().SingleAsync(t => t.Id == work.TaskId);
        Assert.Equal(TaskStatus.PendingConfirmation, task.Status);
        var receipt = await db.Context.AgentDeliveryReceipts.SingleAsync();
        Assert.Equal(AgentDeliveryAcceptanceStatus.PendingReview, receipt.AcceptanceStatus);
        Assert.Contains("其他待处理", receipt.ReviewComment);
        Assert.Equal(1, db.AI.AutomaticReviewCalls);
        Assert.Equal(AgentWorkItemStatus.Completed,
            (await db.Context.AgentWorkItems.AsNoTracking().SingleAsync(w => w.Id == work.Id)).Status);
        Assert.Equal(AgentWorkItemStatus.Pending,
            (await db.Context.AgentWorkItems.AsNoTracking().SingleAsync(w => w.Id != work.Id)).Status);
    }

    [Fact]
    public async Task SimpleAgent_CreateDuplicateNamesGeneratesDistinctKeysAndSafeDefaults()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new AgentSetupService(new AgentAdministrationService(db.Context, new AgentToolCatalog()));
        var input = new AgentSetupInput { Name = "周报助手", Purpose = "report", Instructions = "汇总进度，说明依据和下周计划" };
        var first = await service.SaveAsync(null, input, db.Admin.Id);
        var second = await service.SaveAsync(null, input, db.Admin.Id);
        Assert.NotEqual(first.AgentKey, second.AgentKey);
        Assert.True(first.IsEnabled);
        Assert.Equal(first.Version, first.StableVersion);
        Assert.Equal("", first.ModelName);
        Assert.Equal("report.create", Assert.Single(first.ToolPermissions, p => p.IsEnabled).ToolName);
        Assert.Equal(AgentToolReviewMode.AiReview, first.ToolPermissions.Single(p => p.IsEnabled).ReviewMode);
        Assert.Empty(await db.Context.AgentDocumentPermissions.ToListAsync());
        Assert.Contains(input.Instructions, first.SystemPrompt);
    }

    [Theory]
    [InlineData("failed-tool")]
    [InlineData("rejected-tool")]
    [InlineData("pending-tool")]
    [InlineData("high-risk-tool")]
    [InlineData("missing-search")]
    [InlineData("no-context")]
    [InlineData("max-steps")]
    public async Task AutomaticReview_HardChecksCannotBeOverriddenByPositiveAI(string condition)
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db, condition == "missing-search" ? "联网搜索公开资料，汇总任务管理方法。" : null);
        var task = await db.Context.ToDoTasks.SingleAsync();
        var session = new AiSession { AgentKey = db.Agent.AgentKey, AgentVersion = db.Agent.Version,
            UserId = db.Admin.Id, ProjectId = db.Project.Id, TaskId = task.Id };
        db.Context.Add(session);
        await db.Context.SaveChangesAsync();
        if (condition != "no-context")
            db.Context.Add(new AgentToolCall { AiSessionId = session.Id, ToolName = "context.read", IdempotencyKey = "context-check",
                Status = AgentToolCallStatus.Executed });
        if (condition.EndsWith("tool"))
            db.Context.Add(new AgentToolCall { AiSessionId = session.Id, ToolName = condition == "high-risk-tool" ? "task.update" : "task.add_comment",
                IdempotencyKey = "failed-check", Status = condition switch { "failed-tool" => AgentToolCallStatus.Failed,
                    "rejected-tool" => AgentToolCallStatus.Rejected, "pending-tool" => AgentToolCallStatus.PendingApproval, _ => AgentToolCallStatus.Executed } });
        if (condition == "max-steps") work.StepCount = work.MaxSteps;
        await db.Context.SaveChangesAsync();
        var service = new AgentAutomaticAcceptanceService(db.Context, db.AI, new AgentContextService(db.Context, null!), new AgentToolCatalog());
        var result = await service.EvaluateAsync(work, task, session, db.Agent, AnalysisOutput);
        Assert.False(result.Accepted);
        Assert.Equal(0, db.AI.AutomaticReviewCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticReport_RequiresRealPersistedArtifact(bool exists)
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db, "生成周报并保存到系统，列出当前项目名称和任务进展依据。");
        var task = await db.Context.ToDoTasks.SingleAsync();
        var session = new AiSession { AgentKey = db.Agent.AgentKey, AgentVersion = db.Agent.Version,
            UserId = db.Admin.Id, ProjectId = db.Project.Id, TaskId = task.Id, StartedAt = AppTime.Now.AddMinutes(-1) };
        db.Context.Add(session);
        var report = new DailyReport { ProjectId = db.Project.Id, ReporterId = db.Admin.Id, ReportType = 2,
            ReportTitle = "测试周报", ReportContent = AnalysisOutput };
        if (exists) db.Context.Add(report);
        await db.Context.SaveChangesAsync();
        db.Context.AddRange(
            new AgentToolCall { AiSessionId = session.Id, ToolName = "context.read", IdempotencyKey = "read", Status = AgentToolCallStatus.Executed },
            new AgentToolCall { AiSessionId = session.Id, ToolName = "report.create", IdempotencyKey = "report", Status = AgentToolCallStatus.Executed,
                RiskLevel = AgentRiskLevel.Medium, ReviewMode = AgentToolReviewMode.AiReview,
                ResultJson = JsonSerializer.Serialize(new { message = $"已创建周报 #{(exists ? report.Id : 999)}：测试周报" }) });
        await db.Context.SaveChangesAsync();
        var service = new AgentAutomaticAcceptanceService(db.Context, db.AI, new AgentContextService(db.Context, null!), new AgentToolCatalog());
        var result = await service.EvaluateAsync(work, task, session, db.Agent, AnalysisOutput);
        Assert.Equal(exists, result.Accepted);
        Assert.Equal(exists ? 1 : 0, db.AI.AutomaticReviewCalls);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("changed-tail")]
    public async Task AutomaticReport_ChangedDuringReviewCannotAcceptStaleArtifact(string change)
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db, "生成周报并保存到系统，列出当前项目名称和任务进展依据。");
        // 删除场景不配置 Reports，正文尾部场景配置它但修改摘要 800 字以外的内容。
        db.Agent.ContextSourcesJson = JsonSerializer.Serialize(change == "deleted"
            ? new[] { "Project" } : new[] { "Project", "Reports" });
        var task = await db.Context.ToDoTasks.SingleAsync();
        var session = new AiSession
        {
            AgentKey = db.Agent.AgentKey, AgentVersion = db.Agent.Version, UserId = db.Admin.Id,
            ProjectId = db.Project.Id, TaskId = task.Id, StartedAt = AppTime.Now.AddMinutes(-1)
        };
        var report = new DailyReport
        {
            ProjectId = db.Project.Id, ReporterId = db.Admin.Id, ReportType = 2,
            ReportTitle = "待验收周报", ReportContent = new string('文', 900) + "原始完整结论"
        };
        db.Context.AddRange(session, report);
        await db.Context.SaveChangesAsync();
        db.Context.AddRange(
            new AgentToolCall { AiSessionId = session.Id, ToolName = "context.read", IdempotencyKey = "read", Status = AgentToolCallStatus.Executed },
            new AgentToolCall
            {
                AiSessionId = session.Id, ToolName = "report.create", IdempotencyKey = "report", Status = AgentToolCallStatus.Executed,
                RiskLevel = AgentRiskLevel.Medium, ReviewMode = AgentToolReviewMode.AiReview,
                ResultJson = JsonSerializer.Serialize(new { reportId = report.Id })
            });
        await db.Context.SaveChangesAsync();
        db.AI.BeforeAutomaticReview = async () =>
        {
            if (change == "deleted")
                await db.Context.DailyReport.Where(r => r.Id == report.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsDeleted, true));
            else
                await db.Context.DailyReport.Where(r => r.Id == report.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReportContent, new string('文', 900) + "复核期间被改写的结论"));
        };
        var service = new AgentAutomaticAcceptanceService(db.Context, db.AI,
            new AgentContextService(db.Context, null!), new AgentToolCatalog());

        var result = await service.EvaluateAsync(work, task, session, db.Agent, AnalysisOutput);

        Assert.False(result.Accepted);
        Assert.Contains("报告产物", result.Reason);
        Assert.Equal(1, db.AI.AutomaticReviewCalls);
    }

    [Fact]
    public async Task SimpleAgent_PageRequiresAdministratorAndDoesNotBindTechnicalFields()
    {
        var authorization = Assert.Single(typeof(ToDo.Razor.Pages.Agents.Manage.CreateModel)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());
        Assert.Equal("systemAdmin", authorization.Roles);
        Assert.Equal(new[] { "Instructions", "Name", "Purpose" }, typeof(AgentSetupInput).GetProperties().Select(p => p.Name).Order());
        await using var db = await TestDatabase.CreateAsync();
        var model = new ToDo.Razor.Pages.Agents.Manage.EditModel(null!, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ToDo.Razor.Pages.Agents.Manage.EditModel>.Instance);
        var redirect = Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(await model.OnGetAsync(null));
        Assert.Equal("./Create", redirect.PageName);
    }

    [Theory]
    [InlineData("analysis")]
    [InlineData("report")]
    [InlineData("meeting")]
    [InlineData("documents")]
    [InlineData("research")]
    public async Task SimpleAgent_AllPurposesProduceEnabledConfiguration(string purpose)
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new AgentSetupService(new AgentAdministrationService(db.Context, new AgentToolCatalog()));
        var agent = await service.SaveAsync(null, new() { Name = "助手", Purpose = purpose, Instructions = "整理信息，给出结果及依据" }, db.Admin.Id);
        Assert.True(agent.IsEnabled);
        Assert.True(agent.CanReceiveTaskDispatch);
        Assert.NotNull(agent.AcceptanceContract);
        Assert.DoesNotContain(agent.ToolPermissions, p => p.IsEnabled && p.ReviewMode == AgentToolReviewMode.HumanApproval);
        Assert.Single(await db.Context.AgentDefinitionVersions.Where(v => v.AgentDefinitionId == agent.Id).ToListAsync());
    }

    [Fact]
    public async Task SimpleAgent_EditPreservesModelPauseAndPermissions()
    {
        await using var db = await TestDatabase.CreateAsync();
        var admin = new AgentAdministrationService(db.Context, new AgentToolCatalog());
        var service = new AgentSetupService(admin);
        var agent = await service.SaveAsync(null, new() { Name = "助手", Purpose = "report", Instructions = "整理进度" }, db.Admin.Id);
        agent.ModelName = "existing-model";
        agent.IsEnabled = false;
        agent.ToolPermissions.Single(p => p.IsEnabled).IsEnabled = false;
        await db.Context.SaveChangesAsync();
        var updated = await service.SaveAsync(agent.Id, new() { Name = "新名称", Purpose = "report", Instructions = "整理风险并列出依据" }, db.Admin.Id);
        Assert.False(updated.IsEnabled);
        Assert.Equal("existing-model", updated.ModelName);
        Assert.DoesNotContain(updated.ToolPermissions, p => p.IsEnabled);
        Assert.Equal(2, await db.Context.AgentDefinitionVersions.CountAsync(v => v.AgentDefinitionId == agent.Id));
    }

    [Fact]
    public async Task SimpleAgent_RejectsUnknownPurposeAndDoesNotOverwriteLegacyAgent()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new AgentSetupService(new AgentAdministrationService(db.Context, new AgentToolCatalog()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(null,
            new() { Name = "助手", Purpose = "all-permissions", Instructions = "全部授权" }, db.Admin.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(db.Agent.Id,
            new() { Name = "助手", Purpose = "analysis", Instructions = "覆盖配置" }, db.Admin.Id));
        await Assert.ThrowsAsync<ValidationException>(() => service.SaveAsync(null,
            new() { Name = " ", Purpose = "analysis", Instructions = "工作" }, db.Admin.Id));
        Assert.Equal("队列测试 Agent", db.Agent.Name);
    }

    [Fact]
    public async Task AutomaticReview_UsesContractFromTheExecutionVersion()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new AgentSetupService(new AgentAdministrationService(db.Context, new AgentToolCatalog()));
        var created = await service.SaveAsync(null,
            new() { Name = "助手", Purpose = "analysis", Instructions = "只整理旧目标" }, db.Admin.Id);
        var key = created.AgentKey;
        var id = created.Id;
        await service.SaveAsync(id, new() { Name = "助手", Purpose = "analysis", Instructions = "新的工作目标" }, db.Admin.Id);
        db.Context.ChangeTracker.Clear();
        var registry = new AgentRegistry(db.Context, new AgentToolCatalog());
        var old = await registry.GetForExecutionAsync(key, 1);
        var current = await registry.GetForExecutionAsync(key);
        Assert.Equal("只整理旧目标", old!.AcceptanceContract!.Objective);
        Assert.Equal("新的工作目标", current!.AcceptanceContract!.Objective);
        Assert.NotEmpty(current.AcceptanceContract.RequiredOutput);
    }

    [Fact]
    public async Task AutomaticReview_MetricsDoNotCountAutomaticAcceptanceAsHumanApproval()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db);
        await ExecuteOrdinaryAsync(db, work);
        // 生产为 MySQL；SQLite 不支持既有指标查询的 decimal SUM。
        // 执行链仍在关系型 SQLite 验证，指标投影复制真实结果到隔离 InMemory 进行检查。
        await using var metricsContext = new ToDo.Context.ApplicationDbContext(
            new DbContextOptionsBuilder<ToDo.Context.ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        metricsContext.AddRange(await db.Context.AgentDefinitions.AsNoTracking().ToListAsync());
        metricsContext.AddRange(await db.Context.AiSessions.AsNoTracking().ToListAsync());
        metricsContext.AddRange(await db.Context.AgentDeliveryReceipts.AsNoTracking().ToListAsync());
        await metricsContext.SaveChangesAsync();
        var metrics = new ToDo.Razor.Pages.Agents.Manage.MetricsModel(metricsContext);
        await metrics.OnGetAsync();
        Assert.Equal(1, metrics.AutomaticallyAcceptedDeliveries);
        Assert.Equal(0, metrics.ReviewedDeliveries);
        Assert.Equal(0, metrics.PendingDeliveries);
    }
}

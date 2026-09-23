using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;

namespace ToDo.Test;

public sealed partial class AgentFrameworkCompletionTests
{
    [Theory]
    [InlineData("根据需求分析生成一份概要设计草稿，先给我看，不保存到项目资料。")]
    [InlineData("根据需求分析写一份概要设计文档。")]
    [InlineData("帮我拆分一下这个需求，给出5个任务建议，不要创建任务。")]
    [InlineData("分析当前项目风险和下一步工作。")]
    [InlineData("生成日报，先给我看，不保存到系统。")]
    [InlineData("整理会议纪要和行动项建议。")]
    [InlineData("整理项目资料摘要。")]
    [InlineData("给出任务建议，禁止创建任务。")]
    [InlineData("生成任务计划，不必新建任务。")]
    [InlineData("生成概要设计，不要保存到资料库。")]
    [InlineData("生成周报。")]
    [InlineData("修改任务的建议，只给建议。")]
    [InlineData("整理设计文档，禁止修改项目资料。")]
    [InlineData("整理设计文档，不要修改项目资料。")]
    [InlineData("整理设计文档，不得修改项目资料。")]
    [InlineData("给出创建任务的步骤，仅提供建议。")]
    public void ContentRequests_DoNotGrantWriteToolsOrNeedPlanning(string request)
    {
        var task = new ToDoTask { Title = request, CreatorId = 1 };
        var policy = AgentTaskIntentMatcher.ForTask(task, AgentWorkQueueService.BuildAssignmentPrompt(task));
        Assert.NotEmpty(policy.Intents);
        Assert.False(policy.Writes);
        Assert.False(policy.RequiresPlan);
        Assert.Empty(policy.Tools);
        Assert.False(AgentWorkQueueService.AllowsBusinessToolCalls(task));
        Assert.DoesNotContain(policy.Intents, i => i.RequiredTool != null);
    }

    [Theory]
    [InlineData("把刚才生成的概要设计保存到项目资料库。", "project.document.write")]
    [InlineData("修改项目资料库里现有的概要设计并保存。", "project.document.write")]
    [InlineData("在系统里创建这5个子任务。", "task.create")]
    [InlineData("创建任务。", "task.create")]
    [InlineData("修改任务状态。", "task.update")]
    [InlineData("修改项目名称。", "project.update")]
    [InlineData("创建行动项。", "meeting.action.create")]
    [InlineData("生成日报并保存到系统。", "report.create")]
    [InlineData("将结论写入任务评论。", "task.add_comment")]
    public void ExplicitBusinessActions_RequestOnlyCorrespondingTool(string request, string tool)
    {
        var decision = AgentTaskIntentMatcher.Analyze(request);
        Assert.Equal(tool, Assert.Single(decision.Tools));
        Assert.True(decision.RequiresPlan);
        Assert.False(decision.ConfirmPlan);
        Assert.Contains(decision.Intents, i => i.RequiredTool == tool);
    }

    [Theory]
    [InlineData("先确认计划")]
    [InlineData("先给我执行方案")]
    [InlineData("确认计划后再执行")]
    [InlineData("先不要执行")]
    public async Task ExplicitConfirmation_QueuesPlanAndWaitsWithoutDelivery(string phrase)
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db, "分析当前项目进度，" + phrase);
        Assert.True(work.RequiresPlan);
        Assert.Equal(AgentWorkItemStatus.WaitingPlanConfirmation, work.Status);
        Assert.Null(work.AiSessionId);
        Assert.NotNull(work.PlanningSessionId);
        Assert.Empty(await db.Context.AgentDeliveryReceipts.ToListAsync());
        Assert.Empty(await db.Context.TaskComments.ToListAsync());
        Assert.Equal(0, db.AI.CompletionCalls);
    }

    [Fact]
    public void PublicSearch_DirectWithoutBusinessTools_AndCanBeDisabled()
    {
        var request = "联网查一下 ASP.NET Core 8 的相关最佳实践，并结合当前项目给建议。";
        var policy = AgentTaskIntentMatcher.Analyze(request);
        Assert.Equal("web.search", Assert.Single(policy.Tools));
        Assert.False(policy.RequiresPlan);
        Assert.Empty(AgentTaskIntentMatcher.Analyze(request + "不要联网").Tools);
    }

    [Fact]
    public void PlanFeedback_CannotExpandAuthorization_AndCanRevokeIt()
    {
        var task = new ToDoTask { Title = "创建任务", CreatorId = 1 };
        Assert.Equal("task.create", Assert.Single(AgentTaskIntentMatcher.ForTask(task, feedback: "修改项目名称").Tools));
        Assert.Empty(AgentTaskIntentMatcher.ForTask(task, feedback: "不要创建任务").Tools);
    }

    [Fact]
    public async Task HistoricalPlanFlag_IsPreservedWithoutBulkMigration()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db);
        work.RequiresPlan = true;
        await db.Context.SaveChangesAsync();
        await ExecuteOrdinaryAsync(db, work);
        Assert.NotNull(work.PlanningSessionId);
        Assert.True(work.RequiresPlan);
        Assert.Null(work.AiSessionId);
        Assert.Empty(await db.Context.AgentDeliveryReceipts.ToListAsync());
    }

    [Fact]
    public async Task QueuedReadOnlyTask_ChangedToRequireConfirmation_CannotSkipPlan()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await OrdinaryWorkAsync(db);
        var task = await db.Context.ToDoTasks.SingleAsync();
        task.Description = "修改项目名称，先给我执行方案";
        await db.Context.SaveChangesAsync();
        await ExecuteOrdinaryAsync(db, work);
        Assert.True(work.RequiresPlan);
        Assert.Equal(AgentWorkItemStatus.WaitingPlanConfirmation, work.Status);
        Assert.Null(work.AiSessionId);
        Assert.Equal(0, db.AI.CompletionCalls);
        Assert.Empty(await db.Context.AgentDeliveryReceipts.ToListAsync());
    }

    [Fact]
    public async Task FormalNativeTools_ExposeOnlyRequestedAction_NotAllAgentPermissions()
    {
        await using var db = await TestDatabase.CreateAsync(nativeToolCall: true);
        db.Context.AgentToolPermissions.Add(new() { AgentDefinitionId = db.Agent.Id, ToolName = "task.update",
            IsEnabled = true, ReviewMode = AgentToolReviewMode.HumanApproval });
        var task = await db.Context.ToDoTasks.SingleAsync();
        task.Description = "添加评论并总结";
        task.AssigneeType = TaskAssigneeType.DigitalEmployee;
        task.AgentDefinitionId = db.Agent.Id;
        await db.Context.SaveChangesAsync();
        var work = (await db.AssignmentQueue.EnqueueTaskAsync(task.Id, db.Admin.Id, AgentWorkTriggerType.TaskAssigned,
            task.Id, AgentWorkQueueService.BuildAssignmentPrompt(task), "native-scoped"))!;
        Assert.Equal(work.Id, await db.AssignmentQueue.ClaimNextAsync());
        await ApprovePlanBeforeExecutionAsync(db, work);
        await db.AssignmentQueue.ProcessAsync(work.Id);
        Assert.Equal("task_add_comment", Assert.Single(db.AI.LastCompletionOptions!.Tools).Name);
        Assert.DoesNotContain("- task.update", db.AI.LastCompletionPrompt);
        Assert.Single(await db.Context.TaskComments.ToListAsync());
    }

    [Fact]
    public async Task FormalToolBoundary_BlocksUnrequestedLegacyAction_AndKeepsAudit()
    {
        await using var db = await TestDatabase.CreateAsync(nativeToolCall: true);
        var task = await db.Context.ToDoTasks.SingleAsync();
        task.Title = "创建任务，不要添加评论";
        task.AssigneeType = TaskAssigneeType.DigitalEmployee;
        task.AgentDefinitionId = db.Agent.Id;
        await db.Context.SaveChangesAsync();
        var work = (await db.AssignmentQueue.EnqueueTaskAsync(task.Id, db.Admin.Id, AgentWorkTriggerType.TaskAssigned,
            task.Id, AgentWorkQueueService.BuildAssignmentPrompt(task), "scoped-legacy"))!;
        var session = await db.Sessions.StartAsync(db.Agent.AgentKey, task.Title, db.Admin.Id, db.Project.Id, task.Id);
        work.AiSessionId = session.Id;
        work.Status = AgentWorkItemStatus.Running;
        await db.Context.SaveChangesAsync();
        var result = await db.Tools.ProcessResponseAsync(session, db.Admin.Id,
            """<agent-actions>{"version":1,"actions":[{"callId":"unexpected","tool":"task.add_comment","arguments":{"content":"不应该写入"}}]}</agent-actions>""");
        Assert.Equal(AgentToolCallStatus.Failed, Assert.Single(result.ToolCalls).Status);
        Assert.Empty(await db.Context.TaskComments.ToListAsync());
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("custom")]
    public async Task ProfileEdit_PreservesTechnicalSettingsPausePermissionsAndHistory(string kind)
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new AgentAdministrationService(db.Context, new AgentToolCatalog());
        var agent = await new AgentSetupService(service).SaveAsync(null,
            new() { Name = "报告助手", Purpose = "report", Instructions = "汇总进度" }, db.Admin.Id);
        // 模拟已有用户配置，随后通过统一的业务编辑入口修改。
        agent = await db.Context.AgentDefinitions.Include(a => a.ToolPermissions).Include(a => a.AcceptanceContract).SingleAsync(a => a.Id == agent.Id);
        if (kind == "custom") agent.TemplateKey = "custom-legacy";
        agent.IsEnabled = false;
        agent.ModelName = "existing-model";
        agent.MaxTokens = 2345;
        agent.ToolPermissions.Single(p => p.IsEnabled).IsEnabled = false;
        db.Context.AgentDocumentPermissions.Add(new() { ProjectId = db.Project.Id, AgentKey = agent.AgentKey, CanRead = true });
        await db.Context.SaveChangesAsync();
        var oldVersion = agent.Version;
        var oldContract = agent.AcceptanceContract!.SuccessCriteria;
        var updated = await service.SaveProfileAsync(agent.Id, oldVersion,
            new() { Name = "新名称", Description = "团队周报", Instructions = "列出进展、风险及依据" }, db.Admin.Id);
        Assert.False(updated.IsEnabled);
        Assert.Equal("existing-model", updated.ModelName);
        Assert.Equal(2345, updated.MaxTokens);
        Assert.Equal(oldContract, updated.AcceptanceContract!.SuccessCriteria);
        Assert.DoesNotContain(updated.ToolPermissions, p => p.IsEnabled);
        Assert.Single(await db.Context.AgentDocumentPermissions.ToListAsync());
        Assert.Equal(oldVersion + 1, updated.Version);
        Assert.Equal(updated.Version, updated.StableVersion);
        Assert.Equal(2, await db.Context.AgentDefinitionVersions.CountAsync(v => v.AgentDefinitionId == agent.Id));
        var snapshot = await new AgentDefinitionSnapshotService(db.Context).LoadRuntimeVersionAsync(updated, updated.Version);
        Assert.Equal(updated.SystemPrompt, snapshot!.SystemPrompt);
        Assert.Equal(updated.ModelName, snapshot.ModelName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveProfileAsync(agent.Id, oldVersion,
            new() { Name = "冲突", Description = "冲突", Instructions = "冲突" }, db.Admin.Id));
    }

    [Theory]
    [InlineData("member")]
    [InlineData("disabled-admin")]
    [InlineData("archived")]
    [InlineData("draft")]
    [InlineData("canary")]
    public async Task ProfileEdit_RejectsUnauthorizedOrUnresolvedStates(string state)
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new AgentAdministrationService(db.Context, new AgentToolCatalog());
        if (state == "disabled-admin") db.Admin.Status = UserStatus.Inactive;
        if (state == "archived") db.Agent.LifecycleStatus = AgentLifecycleStatus.Archived;
        if (state == "draft") db.Agent.StableVersion = db.Agent.Version - 1;
        if (state == "canary") db.Agent.DeploymentStatus = AgentDeploymentStatus.Canary;
        await db.Context.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => service.SaveProfileAsync(db.Agent.Id, db.Agent.Version,
            new() { Name = "新名称", Description = "用途", Instructions = "要求" }, state == "member" ? db.Member.Id : db.Admin.Id));
        Assert.Equal("队列测试 Agent", (await service.GetAsync(db.Agent.Id))!.Name);
        Assert.Empty(await db.Context.AgentDefinitionVersions.ToListAsync());
    }

    [Theory]
    [InlineData(typeof(ToDo.Razor.Pages.Agents.Manage.EditModel))]
    [InlineData(typeof(ToDo.Razor.Pages.Agents.Manage.TechnicalModel))]
    public void BothEditingRoutes_RequireAdministrator(Type model)
    {
        var authorization = Assert.Single(model.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());
        Assert.Equal("systemAdmin", authorization.Roles);
        Assert.Equal(new[] { "Description", "Instructions", "Name" }, typeof(AgentProfileInput).GetProperties().Select(p => p.Name).Order());
    }
}

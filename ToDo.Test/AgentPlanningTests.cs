using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Test;

public sealed partial class AgentFrameworkCompletionTests
{
    // 原执行链依然生成真实计划；普通任务自动继续，明确要求确认的任务仍走人工确认。
    private static async Task ApprovePlanBeforeExecutionAsync(TestDatabase db, AgentWorkItem work)
    {
        var current = await db.Context.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id);
        if (!current.RequiresPlan || current.AiSessionId.HasValue || current.PlanApprovedAt.HasValue) return;
        await db.AssignmentQueue.ProcessAsync(work.Id);
        current = await db.Context.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id);
        if (current.Status == AgentWorkItemStatus.WaitingPlanConfirmation)
            await db.Planning.ReviewAsync(work.TaskId, work.Id, current.PlanRevision, db.Admin.Id, true);
        else
        {
            Assert.Equal(AgentWorkItemStatus.Pending, current.Status);
            Assert.NotNull(current.PlanApprovedAt);
            Assert.Null(current.PlanApprovedByUserId);
        }
        Assert.Equal(work.Id, await db.AssignmentQueue.ClaimNextAsync());
    }

    private static async Task<AgentWorkItem> NewPlanAsync(TestDatabase db)
    {
        var task = new ToDoTask
        {
            Title = "整理任务进展", Description = "只读汇总，不写回，先确认计划",
            CreatorId = db.Admin.Id, ReviewerId = db.Admin.Id, ProjectId = db.Project.Id,
            AssigneeType = TaskAssigneeType.DigitalEmployee, AgentDefinitionId = db.Agent.Id, AgentAssignmentVersion = 1
        };
        db.Context.Add(task);
        await db.Context.SaveChangesAsync();
        var work = (await db.AssignmentQueue.EnqueueTaskAsync(task.Id, db.Admin.Id,
            AgentWorkTriggerType.TaskAssigned, task.Id, AgentWorkQueueService.BuildAssignmentPrompt(task), $"plan-test:{task.Id}"))!;
        Assert.Equal(work.Id, await db.AssignmentQueue.ClaimNextAsync());
        await db.AssignmentQueue.ProcessAsync(work.Id);
        return await db.Context.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id);
    }

    [Fact]
    public async Task Plan_UnconfirmedDoesNotExecuteCreateDeliveryOrChangeBusinessStatus()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await NewPlanAsync(db);
        Assert.True(work.RequiresPlan);
        Assert.Equal(AgentWorkItemStatus.WaitingPlanConfirmation, work.Status);
        Assert.Null(work.AiSessionId);
        Assert.NotNull(work.PlanningSessionId);
        Assert.Equal(0, work.StepCount);
        Assert.Empty(await db.Context.AgentDeliveryReceipts.ToListAsync());
        Assert.Empty(await db.Context.TaskComments.ToListAsync());
        var task = await db.Context.ToDoTasks.AsNoTracking().SingleAsync();
        Assert.Equal(ToDo.Entities.TaskStatus.NotStarted, task.Status);
        Assert.Equal(AgentTaskExecutionStatus.AwaitingPlanConfirmation, task.AgentExecutionStatus);
        Assert.Null(await db.AssignmentQueue.ClaimNextAsync());
        await db.AssignmentQueue.RecoverStaleWorkItemsAsync();
        Assert.Equal(AgentWorkItemStatus.WaitingPlanConfirmation,
            (await db.Context.AgentWorkItems.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Plan_DoubleConfirmationOnlyResumesOneWorkItem()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await NewPlanAsync(db);
        await db.Planning.ReviewAsync(work.TaskId, work.Id, work.PlanRevision, db.Admin.Id, true);
        await db.Planning.ReviewAsync(work.TaskId, work.Id, work.PlanRevision, db.Admin.Id, true);
        Assert.Equal(1, await db.Context.AgentWorkItems.CountAsync());
        Assert.Equal(work.Id, await db.AssignmentQueue.ClaimNextAsync());
        Assert.Null(await db.AssignmentQueue.ClaimNextAsync());
        await db.AssignmentQueue.ProcessAsync(work.Id);
        Assert.Single(await db.Context.AgentDeliveryReceipts.ToListAsync());
    }

    [Fact]
    public async Task Plan_RejectRegeneratesNewRevisionAndOldPageCannotApprove()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await NewPlanAsync(db);
        await db.Planning.ReviewAsync(work.TaskId, work.Id, work.PlanRevision, db.Admin.Id, false, "不要联网，只整理现有资料");
        Assert.Equal(work.Id, await db.AssignmentQueue.ClaimNextAsync());
        await db.AssignmentQueue.ProcessAsync(work.Id);
        var updated = await db.Context.AgentWorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(work.PlanRevision + 1, updated.PlanRevision);
        Assert.Null(updated.PlanApprovedAt);
        Assert.Equal(AgentWorkItemStatus.WaitingPlanConfirmation, updated.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Planning.ReviewAsync(work.TaskId, work.Id, work.PlanRevision, db.Admin.Id, true));
    }

    [Theory]
    [InlineData("description")]
    [InlineData("agent-version")]
    [InlineData("assignment")]
    [InlineData("plan")]
    public async Task Plan_ChangedInputsInvalidateConfirmation(string mutation)
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await NewPlanAsync(db);
        var task = await db.Context.ToDoTasks.SingleAsync();
        if (mutation == "description") task.Description = "新需求";
        if (mutation == "agent-version") db.Agent.Version++;
        if (mutation == "assignment") task.AgentAssignmentVersion++;
        if (mutation == "plan") (await db.Context.AgentWorkItems.SingleAsync()).ExecutionPlan = "修改过的计划";
        await db.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Planning.ReviewAsync(work.TaskId, work.Id, work.PlanRevision, db.Admin.Id, true));
        Assert.Null((await db.Context.AgentWorkItems.AsNoTracking().SingleAsync()).PlanApprovedAt);
    }

    [Fact]
    public async Task Plan_UnauthorizedUserCannotApproveAndEndedTaskCannotResume()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await NewPlanAsync(db);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            db.Planning.ReviewAsync(work.TaskId, work.Id, work.PlanRevision, db.Member.Id, true));
        (await db.Context.ToDoTasks.SingleAsync()).SetStatus(ToDo.Entities.TaskStatus.Cancelled);
        await db.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Planning.ReviewAsync(work.TaskId, work.Id, work.PlanRevision, db.Admin.Id, true));
    }

    [Fact]
    public async Task Plan_ApprovedThenTaskChangesMustPlanAgainBeforeExecution()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await NewPlanAsync(db);
        await db.Planning.ReviewAsync(work.TaskId, work.Id, work.PlanRevision, db.Admin.Id, true);
        (await db.Context.ToDoTasks.SingleAsync()).Description = "已改变范围";
        await db.Context.SaveChangesAsync();
        Assert.Equal(work.Id, await db.AssignmentQueue.ClaimNextAsync());
        await db.AssignmentQueue.ProcessAsync(work.Id);
        var updated = await db.Context.AgentWorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(AgentWorkItemStatus.WaitingPlanConfirmation, updated.Status);
        Assert.Null(updated.AiSessionId);
        Assert.Null(updated.PlanApprovedAt);
    }

    [Fact]
    public async Task Plan_WaitingPlanBlocksLaterCommentWorkAndCanBeCancelled()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await NewPlanAsync(db);
        await db.AssignmentQueue.EnqueueTaskAsync(work.TaskId, db.Admin.Id, AgentWorkTriggerType.TaskCommentAdded,
            1, "新的说明", "plan-later-comment");
        Assert.Null(await db.AssignmentQueue.ClaimNextAsync());
        await db.AssignmentQueue.CancelAsync(work.Id, db.Admin.Id);
        Assert.NotNull(await db.AssignmentQueue.ClaimNextAsync());
    }


    [Fact]
    public async Task PlanMigration_UpgradesExistingQueueRowsWithoutEnablingOldPlans()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await NewPlanAsync(db);
        await db.Context.AgentWorkItems.Where(x => x.Id == work.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, AgentWorkItemStatus.Pending));
        db.Context.ChangeTracker.Clear();
        var columns = new[] { "ExecutionPlan", "PlanAgentVersion", "PlanApprovedAt", "PlanApprovedByUserId",
            "PlanContextHash", "PlanFeedback", "PlanRevision", "PlanningSessionId", "RequiresPlan" };
        foreach (var column in columns)
        {
            // 列名仅来自上面的固定白名单，不接收用户输入。
            await using var command = db.Context.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"ALTER TABLE agent_work_items DROP COLUMN {column};";
            await command.ExecuteNonQueryAsync();
        }
        var migration = new ToDo.Context.Migrations.AddAgentExecutionPlans();
        var sql = db.Context.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations,
            db.Context.GetService<IDesignTimeModel>().Model);
        foreach (var command in sql)
            await db.Context.Database.ExecuteSqlRawAsync(command.CommandText);
        var upgraded = await db.Context.AgentWorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(work.Id, upgraded.Id);
        Assert.Equal(work.Prompt, upgraded.Prompt);
        Assert.False(upgraded.RequiresPlan);
        Assert.Equal("", upgraded.ExecutionPlan);
        Assert.Null(upgraded.PlanApprovedAt);
    }

    [Fact]
    public async Task Plan_DisabledUserCannotApprove()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await NewPlanAsync(db);
        db.Admin.IsDeleted = true;
        await db.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            db.Planning.ReviewAsync(work.TaskId, work.Id, work.PlanRevision, db.Admin.Id, true));
    }

    [Fact]
    public async Task Plan_AdjustmentCannotBeSilentlyIgnoredOnConfirmation()
    {
        await using var db = await TestDatabase.CreateAsync();
        var work = await NewPlanAsync(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Planning.ReviewAsync(work.TaskId, work.Id, work.PlanRevision, db.Admin.Id, true, "不要联网"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Planning.ReviewAsync(work.TaskId, work.Id, work.PlanRevision, db.Admin.Id, false, ""));
        Assert.Equal(AgentWorkItemStatus.WaitingPlanConfirmation,
            (await db.Context.AgentWorkItems.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public void Plan_FeedbackCanRestrictWritesWithoutGrantingNewAuthority()
    {
        var task = new ToDoTask { CreatorId = 1, Title = "整理资料", Description = "请写回结果" };
        Assert.False(AgentWorkQueueService.AllowsBusinessToolCalls(task, AgentWorkQueueService.BuildAssignmentPrompt(task), "不要写入"));
        Assert.False(AgentWorkQueueService.AllowsWebSearch(task, "联网搜索\n不要联网"));
    }

    [Theory]
    [InlineData("联网搜索公开资料", true)]
    [InlineData("只读联网搜索公开资料，不写回", true)]
    [InlineData("禁止联网，只汇总已有资料", false)]
    [InlineData("整理资料", false)]
    public void Search_RequiresExplicitUserIntent(string description, bool expected)
    {
        var task = new ToDoTask { CreatorId = 1, Title = "调研", Description = description };
        Assert.Equal(expected, AgentWorkQueueService.AllowsWebSearch(task));
    }
}

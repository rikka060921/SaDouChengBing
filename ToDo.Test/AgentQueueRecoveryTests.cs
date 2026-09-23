using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;

namespace ToDo.Test;

public sealed partial class AgentFrameworkCompletionTests
{
    [Fact]
    public async Task ToolCall_ShutdownBeforeBusinessWritePreservesUncertainFailureAndBlocksAcceptance()
    {
        using var stopping = new CancellationTokenSource();
        await using var db = await TestDatabase.CreateAsync(nativeToolCall: true,
            saveInterceptor: new CancelAfterProposedToolSavedInterceptor(stopping));
        var task = await db.Context.ToDoTasks.SingleAsync();
        task.Title = "分析项目进度";
        task.Description = "整理当前项目进度，说明事实依据";
        await db.Context.SaveChangesAsync();
        var session = await db.Sessions.StartAsync(db.Agent.AgentKey, task.Description, db.Admin.Id,
            db.Project.Id, task.Id, agentVersion: db.Agent.Version);
        var action = new AgentToolAction
        {
            CallId = "interrupted-before-comment",
            Tool = "task.add_comment",
            Arguments = JsonSerializer.SerializeToElement(new { content = "此评论不应写入" })
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            db.Tools.ProcessActionsAsync(session, db.Admin.Id, "", [action], stopping.Token));

        var saved = await db.Context.AgentToolCalls.AsNoTracking().SingleAsync();
        Assert.Equal(AgentToolCallStatus.Failed, saved.Status);
        Assert.NotNull(saved.CompletedAt);
        using var resultJson = JsonDocument.Parse(saved.ResultJson);
        var error = resultJson.RootElement.GetProperty("error").GetString();
        Assert.Contains("执行中断", error);
        Assert.Contains("需核对", error);
        Assert.Empty(await db.Context.TaskComments.AsNoTracking().ToListAsync());

        var replay = await db.Tools.ProcessActionsAsync(session, db.Admin.Id, "", [action]);
        Assert.Equal(saved.Id, Assert.Single(replay.ToolCalls).Id);
        Assert.Equal(AgentToolCallStatus.Failed, replay.ToolCalls[0].Status);
        Assert.Empty(await db.Context.TaskComments.AsNoTracking().ToListAsync());
        Assert.Single(await db.Context.AgentToolCalls.AsNoTracking().ToListAsync());

        var work = new AgentWorkItem
        {
            TaskId = task.Id, ProjectId = db.Project.Id, AgentDefinitionId = db.Agent.Id,
            RequestedByUserId = db.Admin.Id, AiSessionId = session.Id
        };
        var acceptance = new AgentAutomaticAcceptanceService(db.Context, db.AI,
            new AgentContextService(db.Context, null!), new AgentToolCatalog());
        var result = await acceptance.EvaluateAsync(work, task, session, db.Agent, AnalysisOutput);
        Assert.False(result.Accepted);
        Assert.Contains("失败、拒绝或未决工具调用", result.Reason);
        Assert.Equal(0, db.AI.AutomaticReviewCalls);
    }

    private sealed class CancelAfterProposedToolSavedInterceptor(CancellationTokenSource stopping) : SaveChangesInterceptor
    {
        private bool _interrupted;

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (!_interrupted && eventData.Context?.ChangeTracker.Entries<AgentToolCall>()
                    .Any(entry => entry.Entity.Status == AgentToolCallStatus.Proposed) == true)
            {
                _interrupted = true;
                stopping.Cancel();
            }
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData("agent.tool.processed")]
    [InlineData("ai.session.started")]
    public async Task AssignmentQueue_FirstTurnShutdownPreservesSessionAndToolIdempotency(string interruptEvent)
    {
        await using var db = await TestDatabase.CreateAsync(nativeToolCall: true);
        var task = await db.Context.ToDoTasks.SingleAsync();
        task.Description = "添加评论并总结";
        task.AssigneeType = TaskAssigneeType.DigitalEmployee;
        task.AgentDefinitionId = db.Agent.Id;
        task.AgentAssignmentVersion = 1;
        task.ReviewerId = db.Admin.Id;
        await db.Context.SaveChangesAsync();
        var work = (await db.AssignmentQueue.EnqueueTaskAsync(task.Id, db.Admin.Id, AgentWorkTriggerType.TaskAssigned,
            task.Id, "添加评论并总结", $"first-turn-shutdown:{task.Id}"))!;
        work.RequiresPlan = false;
        await db.Context.SaveChangesAsync();
        using var stopping = new CancellationTokenSource();
        var interruptedAfterTool = interruptEvent == "agent.tool.processed";
        db.Events.BeforePublish = async (eventType, token) =>
        {
            if (eventType != interruptEvent) return;
            // 此处从数据库读回，确保“工具后中断”发生在业务写入已经提交之后。
            Assert.Equal(interruptedAfterTool ? 1 : 0, await db.Context.TaskComments.AsNoTracking().CountAsync());
            stopping.Cancel();
            token.ThrowIfCancellationRequested();
        };
        Assert.Equal(work.Id, await db.AssignmentQueue.ClaimNextAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.AssignmentQueue.ProcessAsync(work.Id, stopping.Token));

        var interrupted = await db.Context.AgentWorkItems.AsNoTracking().SingleAsync();
        var originalSession = await db.Context.AiSessions.AsNoTracking().SingleAsync();
        Assert.Equal(originalSession.Id, interrupted.AiSessionId);
        Assert.Equal(AgentWorkItemStatus.Pending, interrupted.Status);
        Assert.Equal(AiSessionStatus.Failed, originalSession.Status);
        Assert.Equal(0, originalSession.TurnCount);
        if (interruptedAfterTool)
            Assert.Equal(AgentToolCallStatus.Executed, (await db.Context.AgentToolCalls.AsNoTracking().SingleAsync()).Status);

        db.Events.BeforePublish = null;
        // 模型重放完全相同的工具请求，证明真正依靠同一 Session/轮次的幂等键防重。
        db.AI.SetCompletion(new AICompletionResult
        {
            Content = string.Empty,
            ModelName = "stub-native",
            ToolCalls =
            [
                new AICompletionToolCall
                {
                    Id = "provider-native-1",
                    Name = "task_add_comment",
                    ArgumentsJson = "{\"content\":\"Native execution succeeded\"}"
                }
            ]
        });
        Assert.Equal(work.Id, await db.AssignmentQueue.ClaimNextAsync());
        await db.AssignmentQueue.ProcessAsync(work.Id);
        if (!interruptedAfterTool)
        {
            await db.Context.AgentWorkItems.Where(item => item.Id == work.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextRunAt, AppTime.Now));
            Assert.Equal(work.Id, await db.AssignmentQueue.ClaimNextAsync());
            await db.AssignmentQueue.ProcessAsync(work.Id);
        }

        var completed = await db.Context.AgentWorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(AgentWorkItemStatus.Completed, completed.Status);
        Assert.Equal(originalSession.Id, completed.AiSessionId);
        Assert.Equal(originalSession.AgentVersion, (await db.Context.AiSessions.AsNoTracking().SingleAsync()).AgentVersion);
        Assert.Single(await db.Context.TaskComments.ToListAsync());
        Assert.Equal(AgentToolCallStatus.Executed, (await db.Context.AgentToolCalls.AsNoTracking().SingleAsync()).Status);
        Assert.Null(await db.AssignmentQueue.ClaimNextAsync());
    }

    [Fact]
    public async Task ManualRunQueue_ContextAuditDoesNotRepeatCompletedAnswer()
    {
        await using var db = await TestDatabase.CreateAsync();
        db.Agent.ContextSourcesJson = "[\"Project\"]";
        await db.Context.SaveChangesAsync();
        var job = await db.Queue.EnqueueNewAsync(db.Agent.AgentKey, "分析项目进展", db.Admin, db.Project.Id, null);

        Assert.Equal(job.Id, await db.Queue.ClaimNextAsync());
        await db.Queue.ProcessAsync(job.Id);

        var saved = await db.Context.AgentRunJobs.AsNoTracking().SingleAsync();
        Assert.Equal(AgentRunJobStatus.Completed, saved.Status);
        Assert.Equal(1, saved.StepCount);
        Assert.Equal(1, (await db.Context.AiSessions.AsNoTracking().SingleAsync()).TurnCount);
        Assert.Equal(AgentContextService.ContextReadToolName, (await db.Context.AgentToolCalls.SingleAsync()).ToolName);
        Assert.Null(await db.Queue.ClaimNextAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualRunQueue_ShutdownAtAttemptLimitResumesWithoutReplayingTool(bool failAfterResume)
    {
        await using var db = await TestDatabase.CreateAsync(nativeToolCall: true);
        db.Agent.ContextSourcesJson = "[\"Project\"]";
        await db.Context.SaveChangesAsync();
        var task = await db.Context.ToDoTasks.SingleAsync();
        var job = await db.Queue.EnqueueNewAsync(db.Agent.AgentKey, "添加评论并总结", db.Admin, db.Project.Id, task.Id);
        Assert.Equal(job.Id, await db.Queue.ClaimNextAsync());
        await db.Queue.ProcessAsync(job.Id);
        Assert.Equal(AgentRunJobStatus.Pending, (await db.Context.AgentRunJobs.AsNoTracking().SingleAsync()).Status);
        Assert.Single(await db.Context.TaskComments.ToListAsync());

        await db.Context.AgentRunJobs.Where(item => item.Id == job.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.AttemptCount, item => item.MaxAttempts)
            .SetProperty(item => item.NextRunAt, AppTime.Now));
        Assert.Equal(job.Id, await db.Queue.ClaimNextAsync());
        using var stopping = new CancellationTokenSource();
        db.AI.BeforeCompletion = token =>
        {
            stopping.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Queue.ProcessAsync(job.Id, stopping.Token));

        var interrupted = await db.Context.AgentRunJobs.AsNoTracking().SingleAsync();
        Assert.Equal(AgentRunJobStatus.Pending, interrupted.Status);
        Assert.Equal(interrupted.MaxAttempts, interrupted.AttemptCount);
        Assert.Equal(job.AiSessionId, interrupted.AiSessionId);
        Assert.Null(interrupted.LockedAt);
        Assert.Null(interrupted.CompletedAt);
        db.AI.BeforeCompletion = failAfterResume ? _ => throw new HttpRequestException("模型仍不可用") : null;
        Assert.Equal(job.Id, await db.Queue.ClaimNextAsync());
        await db.Queue.ProcessAsync(job.Id);

        var completed = await db.Context.AgentRunJobs.AsNoTracking().SingleAsync();
        Assert.Equal(failAfterResume ? AgentRunJobStatus.Failed : AgentRunJobStatus.Completed, completed.Status);
        Assert.Equal(completed.MaxAttempts, completed.AttemptCount);
        Assert.NotNull(completed.CompletedAt);
        Assert.Single(await db.Context.TaskComments.ToListAsync());
        Assert.Single(await db.Context.AgentToolCalls.Where(call => call.ToolName == "task.add_comment").ToListAsync());
        Assert.Single(await db.Context.AiSessions.ToListAsync());
        Assert.Null(await db.Queue.ClaimNextAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssignmentQueue_ShutdownAtAttemptLimitResumesWithoutReplayingTool(bool failAfterResume)
    {
        await using var db = await TestDatabase.CreateAsync(nativeToolCall: true);
        var task = await db.Context.ToDoTasks.SingleAsync();
        task.Description = "添加评论并总结";
        task.AssigneeType = TaskAssigneeType.DigitalEmployee;
        task.AgentDefinitionId = db.Agent.Id;
        task.AgentAssignmentVersion = 1;
        task.ReviewerId = db.Admin.Id;
        await db.Context.SaveChangesAsync();
        var work = (await db.AssignmentQueue.EnqueueTaskAsync(task.Id, db.Admin.Id, AgentWorkTriggerType.TaskAssigned,
            task.Id, "添加评论并总结", $"shutdown:{task.Id}"))!;
        work.RequiresPlan = false;
        await db.Context.SaveChangesAsync();
        Assert.Equal(work.Id, await db.AssignmentQueue.ClaimNextAsync());
        await db.AssignmentQueue.ProcessAsync(work.Id);
        var firstStep = await db.Context.AgentWorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(AgentWorkItemStatus.Pending, firstStep.Status);
        Assert.NotNull(firstStep.AiSessionId);
        Assert.Single(await db.Context.TaskComments.ToListAsync());

        await db.Context.AgentWorkItems.Where(item => item.Id == work.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.AttemptCount, item => item.MaxAttempts)
            .SetProperty(item => item.NextRunAt, AppTime.Now));
        Assert.Equal(work.Id, await db.AssignmentQueue.ClaimNextAsync());
        using var stopping = new CancellationTokenSource();
        db.AI.BeforeCompletion = token =>
        {
            stopping.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.AssignmentQueue.ProcessAsync(work.Id, stopping.Token));

        var interrupted = await db.Context.AgentWorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(AgentWorkItemStatus.Pending, interrupted.Status);
        Assert.Equal(interrupted.MaxAttempts, interrupted.AttemptCount);
        Assert.Equal(firstStep.AiSessionId, interrupted.AiSessionId);
        Assert.Null(interrupted.LockedAt);
        Assert.Null(interrupted.CompletedAt);
        Assert.Equal(AgentTaskExecutionStatus.Pending, (await db.Context.ToDoTasks.AsNoTracking().SingleAsync()).AgentExecutionStatus);
        db.AI.BeforeCompletion = failAfterResume ? _ => throw new HttpRequestException("模型仍不可用") : null;
        Assert.Equal(work.Id, await db.AssignmentQueue.ClaimNextAsync());
        await db.AssignmentQueue.ProcessAsync(work.Id);

        var completed = await db.Context.AgentWorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(failAfterResume ? AgentWorkItemStatus.Failed : AgentWorkItemStatus.Completed, completed.Status);
        Assert.Equal(completed.MaxAttempts, completed.AttemptCount);
        Assert.NotNull(completed.CompletedAt);
        Assert.Single(await db.Context.TaskComments.ToListAsync());
        Assert.Single(await db.Context.AgentToolCalls.Where(call => call.ToolName == "task.add_comment").ToListAsync());
        Assert.Single(await db.Context.AiSessions.ToListAsync());
        Assert.Null(await db.AssignmentQueue.ClaimNextAsync());
    }
}

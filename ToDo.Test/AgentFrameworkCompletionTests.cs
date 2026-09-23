using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Domain.Options;
using ToDo.Entities;

namespace ToDo.Test;

public sealed partial class AgentFrameworkCompletionTests
{
    [Fact]
    public async Task ManualRunQueue_PersistsClaimsAndCompletesOutsideRequest()
    {
        await using var db = await TestDatabase.CreateAsync();

        var job = await db.Queue.EnqueueNewAsync(db.Agent.AgentKey, "分析项目进展", db.Admin, db.Project.Id, null);
        Assert.Equal(AgentRunJobStatus.Pending, job.Status);
        Assert.Equal(db.Agent.Version, (await db.Context.AiSessions.SingleAsync()).AgentVersion);

        var claimed = await db.Queue.ClaimNextAsync();
        Assert.Equal(job.Id, claimed);
        Assert.Null(await db.Queue.ClaimNextAsync());
        await db.Queue.ProcessAsync(job.Id);

        var saved = await db.Context.AgentRunJobs.AsNoTracking().SingleAsync();
        var session = await db.Context.AiSessions.AsNoTracking().SingleAsync();
        Assert.Equal(AgentRunJobStatus.Completed, saved.Status);
        Assert.Equal(AiSessionStatus.WaitingHuman, session.Status);
        Assert.Equal(120, session.InputTokens + session.OutputTokens);
        Assert.Equal("测试完成", saved.ResultSummary);
    }

    [Fact]
    public async Task ManualRunQueue_OnlyConsumesAttemptBudgetWhenRetrying()
    {
        await using var db = await TestDatabase.CreateAsync();
        var job = await db.Queue.EnqueueNewAsync(db.Agent.AgentKey, "执行多步骤任务", db.Admin, db.Project.Id, null);

        Assert.Equal(job.Id, await db.Queue.ClaimNextAsync());
        var firstClaim = await db.Context.AgentRunJobs.AsNoTracking().SingleAsync();
        Assert.Equal(1, firstClaim.AttemptCount);

        await db.Context.AgentRunJobs.Where(item => item.Id == job.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, AgentRunJobStatus.Pending)
            .SetProperty(item => item.NextRunAt, DateTime.Now));
        Assert.Equal(job.Id, await db.Queue.ClaimNextAsync());
        var nextStep = await db.Context.AgentRunJobs.AsNoTracking().SingleAsync();
        Assert.Equal(1, nextStep.AttemptCount);

        await db.Context.AgentRunJobs.Where(item => item.Id == job.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, AgentRunJobStatus.Retrying)
            .SetProperty(item => item.NextRunAt, DateTime.Now));
        Assert.Equal(job.Id, await db.Queue.ClaimNextAsync());
        var retry = await db.Context.AgentRunJobs.AsNoTracking().SingleAsync();
        Assert.Equal(2, retry.AttemptCount);
    }

    [Fact]
    public async Task ManualRunQueue_CanResumeLegacyPendingJobAtAttemptLimit()
    {
        await using var db = await TestDatabase.CreateAsync();
        var job = await db.Queue.EnqueueNewAsync(db.Agent.AgentKey, "继续被旧逻辑卡住的任务", db.Admin, db.Project.Id, null);
        await db.Context.AgentRunJobs.Where(item => item.Id == job.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, AgentRunJobStatus.Pending)
            .SetProperty(item => item.AttemptCount, item => item.MaxAttempts)
            .SetProperty(item => item.NextRunAt, DateTime.Now));

        Assert.Equal(job.Id, await db.Queue.ClaimNextAsync());
        var resumed = await db.Context.AgentRunJobs.AsNoTracking().SingleAsync();
        Assert.Equal(AgentRunJobStatus.Running, resumed.Status);
        Assert.Equal(resumed.MaxAttempts, resumed.AttemptCount);
    }

    [Fact]
    public async Task ManualRunQueue_RejectsUserOutsideProject()
    {
        await using var db = await TestDatabase.CreateAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            db.Queue.EnqueueNewAsync(db.Agent.AgentKey, "越权运行", db.Member, db.Project.Id, null));
    }

    [Fact]
    public async Task ManualRunQueue_BindsAndValidatesExactDeliveryReceipt()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = new ToDoTask
        {
            Title = "精确凭证测试",
            CreatorId = db.Admin.Id,
            ProjectId = db.Project.Id
        };
        db.Context.ToDoTasks.Add(task);
        await db.Context.SaveChangesAsync();
        var sourceSession = await db.Sessions.StartAsync(
            db.Agent.AgentKey,
            "生成交付",
            db.Admin.Id,
            db.Project.Id,
            task.Id,
            agentVersion: db.Agent.Version);
        var workItem = new AgentWorkItem
        {
            AgentDefinitionId = db.Agent.Id,
            ProjectId = db.Project.Id,
            TaskId = task.Id,
            RequestedByUserId = db.Admin.Id,
            AiSessionId = sourceSession.Id,
            IdempotencyKey = "exact-receipt-source",
            Status = AgentWorkItemStatus.Completed
        };
        db.Context.AgentWorkItems.Add(workItem);
        await db.Context.SaveChangesAsync();
        var receipt = new AgentDeliveryReceipt
        {
            AgentWorkItemId = workItem.Id,
            AgentDefinitionId = db.Agent.Id,
            ProjectId = db.Project.Id,
            TaskId = task.Id,
            AiSessionId = sourceSession.Id,
            AgentVersion = db.Agent.Version,
            OutcomeSummary = "指定交付",
            ContentHash = new string('a', 64)
        };
        db.Context.AgentDeliveryReceipts.Add(receipt);
        await db.Context.SaveChangesAsync();

        var job = await db.Queue.EnqueueNewAsync(
            db.Agent.AgentKey,
            "审查指定交付",
            db.Admin,
            db.Project.Id,
            task.Id,
            deliveryReceiptId: receipt.Id);

        Assert.Equal(receipt.Id, job.DeliveryReceiptId);
        var session = await db.Context.AiSessions.AsNoTracking()
            .SingleAsync(item => item.Id == job.AiSessionId);
        Assert.Equal(receipt.Id, session.ContextDeliveryReceiptId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Queue.EnqueueNewAsync(
            db.Agent.AgentKey,
            "错误范围",
            db.Admin,
            db.Project.Id,
            null,
            deliveryReceiptId: receipt.Id));
    }

    [Fact]
    public async Task StructuredToolProtocol_IsIdempotentWithinSameTurn()
    {
        await using var db = await TestDatabase.CreateAsync();
        var session = await db.Sessions.StartAsync(db.Agent.AgentKey, "调用工具", db.Admin.Id, db.Project.Id, null, agentVersion: db.Agent.Version);
        const string response = "结果<agent-actions>{\"version\":1,\"actions\":[{\"callId\":\"call-1\",\"tool\":\"unknown.tool\",\"arguments\":{}}]}</agent-actions>";

        var first = await db.Tools.ProcessResponseAsync(session, db.Admin.Id, response);
        var second = await db.Tools.ProcessResponseAsync(session, db.Admin.Id, response);

        Assert.Equal("结果", first.CleanResponse);
        Assert.Single(first.ToolCalls);
        Assert.Single(second.ToolCalls);
        Assert.Equal(first.ToolCalls[0].Id, second.ToolCalls[0].Id);
        Assert.Equal(1, await db.Context.AgentToolCalls.CountAsync());
    }

    [Fact]
    public async Task NativeToolCall_UsesSamePermissionAuditAndExecutionPipeline()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = new ToDoTask
        {
            Title = "Native tool test",
            CreatorId = db.Admin.Id,
            ProjectId = db.Project.Id
        };
        db.Context.AddRange(
            task,
            new AgentToolPermission
            {
                AgentDefinitionId = db.Agent.Id,
                ToolName = "task.add_comment",
                IsEnabled = true,
                RequiresApproval = false,
                ReviewMode = AgentToolReviewMode.Direct
            });
        await db.Context.SaveChangesAsync();
        var session = await db.Sessions.StartAsync(
            db.Agent.AgentKey,
            "Call native tool",
            db.Admin.Id,
            db.Project.Id,
            task.Id,
            agentVersion: db.Agent.Version);
        var actions = new[]
        {
            new AgentToolAction
            {
                CallId = "native-call-1",
                Tool = "task.add_comment",
                Arguments = JsonSerializer.SerializeToElement(new { taskId = task.Id, content = "Native tool call succeeded" })
            }
        };

        var result = await db.Tools.ProcessActionsAsync(session, db.Admin.Id, string.Empty, actions);

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal(AgentToolCallStatus.Executed, call.Status);
        Assert.Equal("task.add_comment", call.ToolName);
        Assert.Equal("Native tool call succeeded", (await db.Context.TaskComments.SingleAsync()).Content);
        Assert.False(string.IsNullOrWhiteSpace(call.IdempotencyKey));
    }

    [Fact]
    public async Task AgentExecution_MapsProviderFunctionNameAndExecutesNativeToolCall()
    {
        await using var db = await TestDatabase.CreateAsync(nativeToolCall: true);
        var task = await db.Context.ToDoTasks.SingleAsync();
        var job = await db.Queue.EnqueueNewAsync(
            db.Agent.AgentKey,
            "Write a completion comment",
            db.Admin,
            db.Project.Id,
            task.Id);

        Assert.Equal(job.Id, await db.Queue.ClaimNextAsync());
        await db.Queue.ProcessAsync(job.Id);
        await db.Context.AgentRunJobs.Where(item => item.Id == job.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.NextRunAt, DateTime.Now));
        Assert.Equal(job.Id, await db.Queue.ClaimNextAsync());
        await db.Queue.ProcessAsync(job.Id);

        var call = await db.Context.AgentToolCalls.AsNoTracking().SingleAsync();
        Assert.Equal("task.add_comment", call.ToolName);
        Assert.False(string.IsNullOrWhiteSpace(call.IdempotencyKey));
        Assert.Equal(AgentToolCallStatus.Executed, call.Status);
        Assert.Equal("Native execution succeeded", (await db.Context.TaskComments.SingleAsync()).Content);
        Assert.Equal(AgentRunJobStatus.Completed, (await db.Context.AgentRunJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task AssignedTaskQueue_ExecutesAgentAndWritesBackReviewableTaskState()
    {
        await using var db = await TestDatabase.CreateAsync();
        db.Agent.AutoCommentOnCompletion = true;
        db.Agent.ContextSourcesJson = JsonSerializer.Serialize(new[]
        {
            AgentContextSource.Project.ToString(),
            AgentContextSource.SelectedTask.ToString()
        });
        await db.Context.SaveChangesAsync();
        var task = new ToDoTask
        {
            Title = "Assigned Agent task",
            Description = "Execute through the persistent assignment queue",
            CreatorId = db.Admin.Id,
            ReviewerId = db.Admin.Id,
            ProjectId = db.Project.Id,
            AssigneeType = TaskAssigneeType.DigitalEmployee,
            AgentDefinitionId = db.Agent.Id,
            AgentName = db.Agent.Name,
            AgentAssignmentVersion = 1,
            AgentExecutionStatus = AgentTaskExecutionStatus.Pending,
            Status = ToDo.Entities.TaskStatus.NotStarted
        };
        db.Context.ToDoTasks.Add(task);
        await db.Context.SaveChangesAsync();

        var workItem = await db.AssignmentQueue.EnqueueTaskAsync(
            task.Id,
            db.Admin.Id,
            AgentWorkTriggerType.TaskAssigned,
            task.Id,
            AgentWorkQueueService.BuildAssignmentPrompt(task),
            $"assigned-e2e:{task.Id}");

        Assert.NotNull(workItem);
        Assert.Equal(workItem!.Id, await db.AssignmentQueue.ClaimNextAsync());
        await ApprovePlanBeforeExecutionAsync(db, workItem);
        await db.AssignmentQueue.ProcessAsync(workItem.Id);

        db.Context.ChangeTracker.Clear();
        var savedWorkItem = await db.Context.AgentWorkItems.AsNoTracking().SingleAsync();
        var savedTask = await db.Context.ToDoTasks.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal(AgentWorkItemStatus.Completed, savedWorkItem.Status);
        Assert.Equal(1, savedWorkItem.StepCount);
        Assert.NotNull(savedWorkItem.AiSessionId);
        Assert.False(string.IsNullOrWhiteSpace(savedWorkItem.ResultSummary));
        Assert.Equal(AgentTaskExecutionStatus.AwaitingConfirmation, savedTask.AgentExecutionStatus);
        Assert.Equal(ToDo.Entities.TaskStatus.PendingConfirmation, savedTask.Status);
        Assert.False(savedTask.IsCompleted);
        Assert.Contains(await db.Context.TaskComments.AsNoTracking().ToListAsync(),
            comment => comment.TaskId == task.Id && comment.IsAiGenerated);
        Assert.Contains(await db.Context.AgentToolCalls.AsNoTracking().ToListAsync(),
            call => call.ToolName == "context.read"
                && call.Status == AgentToolCallStatus.Executed
                && call.RiskLevel == AgentRiskLevel.Low
                && call.ReviewMode == AgentToolReviewMode.Direct
                && !call.RequiresApproval);
    }

    [Fact]
    public void AssignmentPrompt_OnlyAllowsToolsWhenTaskExplicitlyRequestsWriteBack()
    {
        var prompt = AgentWorkQueueService.BuildAssignmentPrompt(new ToDoTask
        {
            Id = 42,
            Title = "项目只读汇总",
            Description = "汇总项目进展并给出建议",
            CreatorId = 1
        });

        Assert.Contains("只有任务描述明确要求把结果写回系统时才允许调用工具", prompt);
        Assert.Contains("不得调用 task.add_comment 或其他写入工具", prompt);
    }

    [Fact]
    public void AssignmentToolBoundary_DeniesReadOnlyAndAllowsExplicitWriteBack()
    {
        Assert.False(AgentWorkQueueService.AllowsBusinessToolCalls(new ToDoTask
        {
            Title = "项目简报",
            Description = "请只读取当前项目，不修改任何数据",
            CreatorId = 1
        }));
        Assert.True(AgentWorkQueueService.AllowsBusinessToolCalls(new ToDoTask
        {
            Title = "同步结论",
            Description = "请将最终结论写入当前任务评论",
            CreatorId = 1
        }));
        Assert.True(AgentWorkQueueService.AllowsBusinessToolCalls(
            new ToDoTask { Title = "Custom queue prompt", CreatorId = 1 },
            "Write the requested completion comment, then summarize the result"));
        Assert.True(AgentWorkQueueService.AllowsBusinessToolCalls(new ToDoTask
        {
            Title = "生成日报并保存到系统",
            CreatorId = 1
        }));
    }

    [Fact]
    public async Task AssignedReadOnlyTask_DoesNotExposeOrExecuteNativeWriteTool()
    {
        await using var db = await TestDatabase.CreateAsync(nativeToolCall: true);
        var task = await db.Context.ToDoTasks.SingleAsync();
        task.Description = "请只读取项目并汇总，不修改、不写回任何数据";
        task.AssigneeType = TaskAssigneeType.DigitalEmployee;
        task.AgentDefinitionId = db.Agent.Id;
        task.AgentName = db.Agent.Name;
        task.AgentAssignmentVersion = 1;
        task.AgentExecutionStatus = AgentTaskExecutionStatus.Pending;
        await db.Context.SaveChangesAsync();

        var workItem = await db.AssignmentQueue.EnqueueTaskAsync(
            task.Id,
            db.Admin.Id,
            AgentWorkTriggerType.TaskAssigned,
            task.Id,
            AgentWorkQueueService.BuildAssignmentPrompt(task),
            $"read-only-boundary:{task.Id}");

        Assert.NotNull(workItem);
        Assert.Equal(workItem!.Id, await db.AssignmentQueue.ClaimNextAsync());
        await ApprovePlanBeforeExecutionAsync(db, workItem);
        await db.AssignmentQueue.ProcessAsync(workItem.Id);

        Assert.Empty(await db.Context.TaskComments.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(await db.Context.AgentToolCalls.AsNoTracking().ToListAsync(),
            call => call.ToolName == "task.add_comment");
        Assert.Equal(AgentWorkItemStatus.Completed,
            (await db.Context.AgentWorkItems.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task AssignedTaskQueue_ExecutesNativeToolThenContinuesToFinalResult()
    {
        await using var db = await TestDatabase.CreateAsync(nativeToolCall: true);
        db.Agent.AutoCommentOnCompletion = true;
        var task = await db.Context.ToDoTasks.SingleAsync();
        task.AssigneeType = TaskAssigneeType.DigitalEmployee;
        task.AgentDefinitionId = db.Agent.Id;
        task.AgentName = db.Agent.Name;
        task.AgentAssignmentVersion = 1;
        task.AgentExecutionStatus = AgentTaskExecutionStatus.Pending;
        task.ReviewerId = db.Admin.Id;
        await db.Context.SaveChangesAsync();

        var workItem = await db.AssignmentQueue.EnqueueTaskAsync(
            task.Id,
            db.Admin.Id,
            AgentWorkTriggerType.TaskAssigned,
            task.Id,
            "Write the requested completion comment, then summarize the result",
            $"assigned-tool-e2e:{task.Id}");
        Assert.NotNull(workItem);

        Assert.Equal(workItem!.Id, await db.AssignmentQueue.ClaimNextAsync());
        await ApprovePlanBeforeExecutionAsync(db, workItem);
        await db.AssignmentQueue.ProcessAsync(workItem.Id);
        var afterTool = await db.Context.AgentWorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(AgentWorkItemStatus.Pending, afterTool.Status);
        Assert.Equal(1, afterTool.StepCount);
        Assert.Equal("Native execution succeeded", (await db.Context.TaskComments.SingleAsync()).Content);

        await db.Context.AgentWorkItems
            .Where(item => item.Id == workItem.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextRunAt, AppTime.Now));
        Assert.Equal(workItem.Id, await db.AssignmentQueue.ClaimNextAsync());
        await ApprovePlanBeforeExecutionAsync(db, workItem);
        await db.AssignmentQueue.ProcessAsync(workItem.Id);

        db.Context.ChangeTracker.Clear();
        var completed = await db.Context.AgentWorkItems.AsNoTracking().SingleAsync();
        var savedTask = await db.Context.ToDoTasks.AsNoTracking().SingleAsync();
        Assert.Equal(AgentWorkItemStatus.Completed, completed.Status);
        Assert.Equal(2, completed.StepCount);
        Assert.Equal(AgentTaskExecutionStatus.AwaitingConfirmation, savedTask.AgentExecutionStatus);
        Assert.Equal(ToDo.Entities.TaskStatus.PendingConfirmation, savedTask.Status);
    }

    [Fact]
    public async Task AssignedTaskQueue_DisabledAutoCommentDoesNotWriteCompletionComment()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = new ToDoTask
        {
            Title = "No automatic comment",
            CreatorId = db.Admin.Id,
            ReviewerId = db.Admin.Id,
            ProjectId = db.Project.Id,
            AssigneeType = TaskAssigneeType.DigitalEmployee,
            AgentDefinitionId = db.Agent.Id,
            AgentName = db.Agent.Name,
            AgentAssignmentVersion = 1,
            AgentExecutionStatus = AgentTaskExecutionStatus.Pending
        };
        db.Context.ToDoTasks.Add(task);
        await db.Context.SaveChangesAsync();

        var workItem = await db.AssignmentQueue.EnqueueTaskAsync(
            task.Id,
            db.Admin.Id,
            AgentWorkTriggerType.TaskAssigned,
            task.Id,
            "Complete without writing a comment",
            $"assigned-no-comment:{task.Id}");

        Assert.NotNull(workItem);
        Assert.Equal(workItem!.Id, await db.AssignmentQueue.ClaimNextAsync());
        await ApprovePlanBeforeExecutionAsync(db, workItem);
        await db.AssignmentQueue.ProcessAsync(workItem.Id);

        Assert.Empty(await db.Context.TaskComments.AsNoTracking().ToListAsync());
        Assert.Equal(
            AgentWorkItemStatus.Completed,
            (await db.Context.AgentWorkItems.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task AssignedTaskQueue_NotificationFailureDoesNotReverseCompletedState()
    {
        await using var db = await TestDatabase.CreateAsync(failNotificationWrites: true);
        var task = new ToDoTask
        {
            Title = "Notification failure",
            CreatorId = db.Admin.Id,
            ReviewerId = db.Admin.Id,
            ProjectId = db.Project.Id,
            AssigneeType = TaskAssigneeType.DigitalEmployee,
            AgentDefinitionId = db.Agent.Id,
            AgentName = db.Agent.Name,
            AgentAssignmentVersion = 1,
            AgentExecutionStatus = AgentTaskExecutionStatus.Pending
        };
        db.Context.ToDoTasks.Add(task);
        await db.Context.SaveChangesAsync();

        var workItem = await db.AssignmentQueue.EnqueueTaskAsync(
            task.Id,
            db.Admin.Id,
            AgentWorkTriggerType.TaskAssigned,
            task.Id,
            "Complete even if notification delivery fails",
            $"assigned-notification-failure:{task.Id}");

        Assert.NotNull(workItem);
        Assert.Equal(workItem!.Id, await db.AssignmentQueue.ClaimNextAsync());
        await ApprovePlanBeforeExecutionAsync(db, workItem);
        await db.AssignmentQueue.ProcessAsync(workItem.Id);

        db.Context.ChangeTracker.Clear();
        var savedWorkItem = await db.Context.AgentWorkItems.AsNoTracking().SingleAsync();
        var savedTask = await db.Context.ToDoTasks.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        var savedSession = await db.Context.AiSessions.AsNoTracking().SingleAsync(x => x.Id == savedWorkItem.AiSessionId);
        Assert.Equal(AgentWorkItemStatus.Completed, savedWorkItem.Status);
        Assert.Equal(AgentTaskExecutionStatus.AwaitingConfirmation, savedTask.AgentExecutionStatus);
        Assert.Equal(ToDo.Entities.TaskStatus.PendingConfirmation, savedTask.Status);
        Assert.Equal(AiSessionStatus.Succeeded, savedSession.Status);
    }

    [Fact]
    public void NativeToolDefinitions_HaveProviderSafeNamesAndValidSchemas()
    {
        foreach (var tool in new AgentToolCatalog().GetAll())
        {
            Assert.Matches("^[A-Za-z0-9_-]{1,64}$", tool.NativeFunctionName);
            using var schema = JsonDocument.Parse(tool.ParametersJsonSchema);
            Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
        }
    }

    [Fact]
    public async Task TaskBoundSession_CannotOperateSiblingTaskInSameProject()
    {
        await using var db = await TestDatabase.CreateAsync();
        var assignedTask = new ToDoTask
        {
            Title = "Assigned task",
            CreatorId = db.Admin.Id,
            ProjectId = db.Project.Id
        };
        var siblingTask = new ToDoTask
        {
            Title = "Sibling task",
            CreatorId = db.Admin.Id,
            ProjectId = db.Project.Id
        };
        db.Context.AddRange(
            assignedTask,
            siblingTask,
            new AgentToolPermission
            {
                AgentDefinitionId = db.Agent.Id,
                ToolName = "task.add_comment",
                IsEnabled = true,
                ReviewMode = AgentToolReviewMode.Direct
            });
        await db.Context.SaveChangesAsync();
        var session = await db.Sessions.StartAsync(
            db.Agent.AgentKey,
            "Only work on the assigned task",
            db.Admin.Id,
            db.Project.Id,
            assignedTask.Id,
            agentVersion: db.Agent.Version);
        var actions = new[]
        {
            new AgentToolAction
            {
                CallId = "cross-task-comment",
                Tool = "task.add_comment",
                Arguments = JsonSerializer.SerializeToElement(new
                {
                    taskId = siblingTask.Id,
                    content = "Must not be written"
                })
            }
        };

        var result = await db.Tools.ProcessActionsAsync(session, db.Admin.Id, string.Empty, actions);

        Assert.Equal(AgentToolCallStatus.Failed, Assert.Single(result.ToolCalls).Status);
        Assert.Empty(await db.Context.TaskComments.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task HighRiskAgentRequest_RequiresIndependentApproverAndDetectsTampering()
    {
        await using var db = await TestDatabase.CreateAsync();
        db.Admin.Role = UserRole.teamMember;
        await db.Context.SaveChangesAsync();
        var signing = new IntegritySigningService(Options.Create(new IntegritySigningOptions
        {
            CurrentKeyId = "test-v1",
            CurrentKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray())
        }));
        var service = new ApprovalRequestService(db.Context, null!, null!, null!, signing);
        var request = await service.RequestAsync(
            db.Project.Id,
            db.Admin.Id,
            "AgentToolCall",
            42,
            ApprovalRequestService.AgentTaskUpdate,
            "Update task",
            "{\"taskId\":1,\"status\":\"InProgress\"}",
            riskLevel: AgentRiskLevel.High,
            reviewMode: AgentToolReviewMode.HumanApproval,
            notifyApprovers: false);

        Assert.True(ApprovalRequestService.VerifyPayloadIntegrity(request));
        Assert.True(service.VerifyPayloadAuthenticity(request));
        Assert.Equal("test-v1", request.SignatureKeyId);
        var signedPayload = request.PayloadSignature;
        request.PayloadSignature = string.Empty;
        request.SignatureKeyId = "legacy-hash-only";
        Assert.False(service.VerifyPayloadAuthenticity(request));
        request.PayloadSignature = signedPayload;
        request.SignatureKeyId = "test-v1";
        Assert.False(await service.CanApproveRequestAsync(request, db.Admin));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ApproveAsync(request.Id, db.Admin, "Self approve"));

        request.PayloadJson = "{\"taskId\":999,\"status\":\"Completed\"}";
        Assert.False(ApprovalRequestService.VerifyPayloadIntegrity(request));
        Assert.False(service.VerifyPayloadAuthenticity(request));
    }

    [Theory]
    [InlineData("{\"decision\":\"approve\",\"reason\":\"普通新增\"}", AgentAutomatedReviewDecision.Approve)]
    [InlineData("{\"decision\":\"reject\",\"reason\":\"包含覆盖历史\"}", AgentAutomatedReviewDecision.Reject)]
    [InlineData("not-json", AgentAutomatedReviewDecision.Escalate)]
    public async Task MediumRiskReview_ParsesDecisionAndFailsClosed(string aiResponse, AgentAutomatedReviewDecision expected)
    {
        var service = new AgentRiskPolicyService(new StubAIService(aiResponse));
        var descriptor = new AgentToolCatalog().Get("task.create")!;
        var result = await service.ReviewAsync(descriptor, "新增任务", "{}");
        Assert.Equal(expected, result.Decision);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private TestDatabase(SqliteConnection connection, ApplicationDbContext context, ApplicationUser admin,
            ApplicationUser member, Project project, AgentDefinition agent, AiSessionService sessions,
            AgentToolService tools, AgentRunQueueService queue, AgentWorkQueueService assignmentQueue, AgentExecutionService execution, StubAIService ai, StubEventBus events)
        {
            _connection = connection; Context = context; Admin = admin; Member = member; Project = project;
            Agent = agent; Sessions = sessions; Tools = tools; Queue = queue; AssignmentQueue = assignmentQueue;
            Planning = new AgentPlanningService(context, execution); Execution = execution; AI = ai; Events = events;
        }
        public ApplicationDbContext Context { get; }
        public ApplicationUser Admin { get; }
        public ApplicationUser Member { get; }
        public Project Project { get; }
        public AgentDefinition Agent { get; }
        public AiSessionService Sessions { get; }
        public AgentToolService Tools { get; }
        public AgentRunQueueService Queue { get; }
        public AgentWorkQueueService AssignmentQueue { get; }
        public AgentPlanningService Planning { get; }
        public AgentExecutionService Execution { get; }
        public StubAIService AI { get; }
        public StubEventBus Events { get; }

        public static async Task<TestDatabase> CreateAsync(
            bool nativeToolCall = false,
            bool failNotificationWrites = false, IAgentWebSearch? search = null,
            SaveChangesInterceptor? saveInterceptor = null, string? connectionString = null)
        {
            var connection = new SqliteConnection(connectionString ?? "Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection);
            if (failNotificationWrites)
                options.AddInterceptors(new RejectNotificationSaveInterceptor());
            if (saveInterceptor != null) options.AddInterceptors(saveInterceptor);
            var context = new ApplicationDbContext(options.Options);
            await context.Database.EnsureCreatedAsync();
            var admin = new ApplicationUser { UserName = "agent-admin", NormalizedUserName = "AGENT-ADMIN", RealName = "管理员", Role = UserRole.systemAdmin, Status = UserStatus.Active };
            var member = new ApplicationUser { UserName = "agent-member", NormalizedUserName = "AGENT-MEMBER", RealName = "成员", Role = UserRole.teamMember, Status = UserStatus.Active };
            context.Users.AddRange(admin, member);
            await context.SaveChangesAsync();
            var project = new Project { Name = "Agent 测试项目", Description = "测试", Requirements = "测试", CreatedByUserId = admin.Id, LeaderUserId = admin.Id };
            var agent = new AgentDefinition { AgentKey = "queue-test-agent", Name = "队列测试 Agent", Description = "测试", SystemPrompt = "回答测试完成", RequiresProject = true, IsEnabled = true, Version = 3 };
            context.AddRange(project, agent);
            await context.SaveChangesAsync();

            var ai = new StubAIService("测试完成");
            if (nativeToolCall)
            {
                var task = new ToDoTask { Title = "Native tool test", CreatorId = admin.Id, ProjectId = project.Id };
                var permission = new AgentToolPermission
                {
                    AgentDefinitionId = agent.Id,
                    ToolName = "task.add_comment",
                    IsEnabled = true,
                    RequiresApproval = false,
                    ReviewMode = AgentToolReviewMode.Direct
                };
                context.AddRange(task, permission);
                await context.SaveChangesAsync();
                ai.SetCompletion(new AICompletionResult
                {
                    Content = string.Empty,
                    ModelName = "stub-native",
                    InputTokens = 100,
                    OutputTokens = 20,
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
            }
            var eventBus = new StubEventBus();
            var sessions = new AiSessionService(context);
            var catalog = new AgentToolCatalog();
            var tools = new AgentToolService(context, null!, null!, sessions, eventBus, catalog, new AgentRiskPolicyService(ai), webSearch: search);
            var registry = new AgentRegistry(context, catalog);
            var execution = new AgentExecutionService(registry, ai, sessions, eventBus, context,
                new AgentContextService(context, null!), catalog, tools);
            var queue = new AgentRunQueueService(context, registry, sessions, execution, eventBus,
                NullLogger<AgentRunQueueService>.Instance);
            var assignmentQueue = new AgentWorkQueueService(
                context,
                execution,
                sessions,
                new UserNotificationService(context),
                eventBus,
                new AgentOutcomeService(context),
                NullLogger<AgentWorkQueueService>.Instance,
                automaticAcceptance: new AgentAutomaticAcceptanceService(context, ai, new AgentContextService(context, null!), catalog));
            return new TestDatabase(connection, context, admin, member, project, agent, sessions, tools, queue, assignmentQueue, execution, ai, eventBus);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RejectNotificationSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<UserNotification>()
                    .Any(entry => entry.State == EntityState.Added) == true)
                throw new DbUpdateException("Simulated notification persistence failure");

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class StubEventBus : IEventBus
    {
        public Func<string, CancellationToken, Task>? BeforePublish { get; set; }
        public async Task<EventBusMessage> PublishAsync(string eventType, object payload, string aggregateType = "", string aggregateId = "", CancellationToken cancellationToken = default)
        {
            if (BeforePublish != null) await BeforePublish(eventType, cancellationToken);
            return new EventBusMessage { EventType = eventType, AggregateType = aggregateType, AggregateId = aggregateId };
        }
        public Task<List<EventBusMessage>> GetRecentAsync(int take = 100, CancellationToken cancellationToken = default) => Task.FromResult(new List<EventBusMessage>());
    }

    private sealed class StubAIService : IAIService
    {
        public Task<AIMeetingMinutesResult> GenerateMeetingMinutesAsync(string transcript, string template, int projectId, CancellationToken cancellationToken = default)
    => throw new NotSupportedException();

        private AICompletionResult _completion;
        private readonly AICompletionResult _fallbackCompletion;

        public StubAIService(string response)
        {
            _fallbackCompletion = new AICompletionResult { Content = response, ModelName = "stub", InputTokens = 100, OutputTokens = 20 };
            _completion = _fallbackCompletion;
        }

        public void SetCompletion(AICompletionResult completion) => _completion = completion;
        public Func<CancellationToken, Task>? BeforeCompletion { get; set; }
        public AIChatOptions? LastCompletionOptions { get; private set; }
        public string LastCompletionPrompt { get; private set; } = string.Empty;
        public int CompletionCalls { get; private set; }
        public string AutomaticReviewResponse { get; set; } = "{}";
        public Func<Task>? BeforeAutomaticReview { get; set; }
        public int AutomaticReviewCalls { get; private set; }
        public AIChatOptions? AutomaticReviewOptions { get; private set; }

        public Task<string> GetChatCompletionAsync(string prompt) => Task.FromResult(_completion.Content);
        public Task<string> GetChatCompletionAsync(string prompt, AIChatOptions options, CancellationToken cancellationToken = default) => Task.FromResult(_completion.Content);
        public async Task<AICompletionResult> GetChatCompletionWithUsageAsync(string prompt, AIChatOptions options, CancellationToken cancellationToken = default)
        {
            if (prompt.Contains("【独立自动验收】"))
            {
                AutomaticReviewCalls++;
                AutomaticReviewOptions = options;
                if (BeforeAutomaticReview != null) await BeforeAutomaticReview();
                return new AICompletionResult { Content = AutomaticReviewResponse, InputTokens = 50, OutputTokens = 30 };
            }
            if (prompt.Contains("【执行计划阶段】"))
                return new AICompletionResult { Content = "目标：整理信息\n1. 读取授权资料\n2. 输出摘要\n3. 系统检查", ModelName = "stub-plan" };
            LastCompletionOptions = options;
            LastCompletionPrompt = prompt;
            CompletionCalls++;
            if (BeforeCompletion != null) await BeforeCompletion(cancellationToken);
            var result = _completion;
            if (result.ToolCalls.Count > 0) _completion = _fallbackCompletion;
            return result;
        }
        public Task<AITaskSplitResult> SplitTaskAsync(string taskTitle, string taskDescription, string? expectedSubTaskCount = null) => throw new NotSupportedException();
        public Task<AIMeetingSummaryResult> ProcessMeetingMinutesAsync(string meetingContent) => throw new NotSupportedException();
        public Task<AIMeetingFullParseResult> ProcessMeetingMinutesFullStructAsync(string meetingContent, List<string>? memberNames = null, List<string>? projectNames = null) => throw new NotSupportedException();
        public Task<AIDailyReportResult> GenerateDailyReportAsync(AIDailyReportInput input) => throw new NotSupportedException();
        public Task<AIDailyReportResult> GenerateDailyReportByProjectAsync(DailyReportByProjectInput input) => throw new NotSupportedException();
        public Task<AIDailyReportResult> GenerateTeamReportAsync(TeamReportInput input) => throw new NotSupportedException();
        public Task<AITaskParseResult> ParseTaskTextAsync(string text, string? expectedCount = null) => throw new NotSupportedException();
    }
}

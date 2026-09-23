using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;
using TaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Test;

public class AgentWorkQueueManagementTests
{
    [Fact]
    public async Task PauseResumeCancel_UpdatesWorkItemAndTaskState()
    {
        await using var database = await QueueDatabase.CreateAsync(AgentWorkItemStatus.Pending);

        await database.Queue.PauseAsync(database.WorkItemId, database.AdminUserId);
        Assert.Equal(AgentWorkItemStatus.Paused, await database.WorkItemStatusAsync());
        Assert.Equal(AgentTaskExecutionStatus.Paused, await database.TaskStatusAsync());

        await database.Queue.ResumeAsync(database.WorkItemId, database.AdminUserId);
        Assert.Equal(AgentWorkItemStatus.Pending, await database.WorkItemStatusAsync());
        Assert.Equal(AgentTaskExecutionStatus.Pending, await database.TaskStatusAsync());

        await database.Queue.CancelAsync(database.WorkItemId, database.AdminUserId);
        Assert.Equal(AgentWorkItemStatus.Cancelled, await database.WorkItemStatusAsync());
        Assert.Equal(AgentTaskExecutionStatus.None, await database.TaskStatusAsync());
        Assert.Equal(3, database.Events.Messages.Count);
    }

    [Fact]
    public async Task Retry_FailedWorkItemCreatesAuditableManualRetryItem()
    {
        await using var database = await QueueDatabase.CreateAsync(AgentWorkItemStatus.Failed);

        var retried = await database.Queue.RetryAsync(database.WorkItemId, database.AdminUserId);

        Assert.NotEqual(database.WorkItemId, retried.Id);
        Assert.Equal(AgentWorkTriggerType.ManualRetry, retried.TriggerType);
        Assert.Equal(database.WorkItemId, retried.TriggerEntityId);
        Assert.Equal(AgentWorkItemStatus.Pending, retried.Status);
        Assert.Equal($"manual-retry:{database.WorkItemId}", retried.IdempotencyKey);
        Assert.Equal(AgentWorkItemStatus.Failed, await database.WorkItemStatusAsync());
        Assert.Equal(AgentTaskExecutionStatus.Pending, await database.TaskStatusAsync());
    }

    [Fact]
    public async Task ManagementAction_RejectsNonAdminUser()
    {
        await using var database = await QueueDatabase.CreateAsync(AgentWorkItemStatus.Pending);
        var member = new ApplicationUser
        {
            UserName = "queue-member",
            RealName = "普通成员",
            Role = UserRole.teamMember,
            Status = UserStatus.Active
        };
        database.Context.Users.Add(member);
        await database.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            database.Queue.PauseAsync(database.WorkItemId, member.Id));
    }

    [Fact]
    public async Task ResumeResolvedApprovals_RequeuesSameWorkItemAndSession()
    {
        await using var database = await QueueDatabase.CreateAsync(AgentWorkItemStatus.WaitingApproval);
        var sessionId = await database.AttachToolCallAsync(AgentToolCallStatus.Executed);

        await database.Queue.ResumeResolvedApprovalsAsync();

        Assert.Equal(AgentWorkItemStatus.Pending, await database.WorkItemStatusAsync());
        Assert.Equal(AgentTaskExecutionStatus.Pending, await database.TaskStatusAsync());
        Assert.Equal(sessionId, await database.Context.AgentWorkItems.AsNoTracking()
            .Where(item => item.Id == database.WorkItemId)
            .Select(item => item.AiSessionId)
            .SingleAsync());
    }

    [Fact]
    public async Task ClaimNext_MultiStepPendingWorkDoesNotConsumeRetryBudget()
    {
        await using var database = await QueueDatabase.CreateAsync(AgentWorkItemStatus.Pending);
        await database.Context.AgentWorkItems
            .Where(item => item.Id == database.WorkItemId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.AttemptCount, 3)
                .SetProperty(item => item.MaxAttempts, 3)
                .SetProperty(item => item.StepCount, 1)
                .SetProperty(item => item.NextRunAt, DateTime.Now.AddMinutes(-1)));

        var claimedId = await database.Queue.ClaimNextAsync();

        Assert.Equal(database.WorkItemId, claimedId);
        var claimed = await database.Context.AgentWorkItems.AsNoTracking()
            .SingleAsync(item => item.Id == database.WorkItemId);
        Assert.Equal(AgentWorkItemStatus.Running, claimed.Status);
        Assert.Equal(3, claimed.AttemptCount);
        Assert.Equal(AgentTaskExecutionStatus.Running, await database.TaskStatusAsync());
    }

    [Fact]
    public async Task ClaimNext_RetryingWorkConsumesOneRetryAttempt()
    {
        await using var database = await QueueDatabase.CreateAsync(AgentWorkItemStatus.Retrying);
        await database.Context.AgentWorkItems
            .Where(item => item.Id == database.WorkItemId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.AttemptCount, 1)
                .SetProperty(item => item.MaxAttempts, 3)
                .SetProperty(item => item.NextRunAt, DateTime.Now.AddMinutes(-1)));

        var claimedId = await database.Queue.ClaimNextAsync();

        Assert.Equal(database.WorkItemId, claimedId);
        var claimed = await database.Context.AgentWorkItems.AsNoTracking()
            .SingleAsync(item => item.Id == database.WorkItemId);
        Assert.Equal(AgentWorkItemStatus.Running, claimed.Status);
        Assert.Equal(2, claimed.AttemptCount);
    }

    [Fact]
    public async Task ClaimNext_DoesNotRunTwoWorkItemsForSameTaskConcurrently()
    {
        await using var database = await QueueDatabase.CreateAsync(AgentWorkItemStatus.Running);
        var first = await database.Context.AgentWorkItems.AsNoTracking()
            .SingleAsync(item => item.Id == database.WorkItemId);
        var second = new AgentWorkItem
        {
            AgentDefinitionId = first.AgentDefinitionId,
            ProjectId = first.ProjectId,
            TaskId = first.TaskId,
            RequestedByUserId = first.RequestedByUserId,
            TriggerType = AgentWorkTriggerType.TaskCommentAdded,
            TriggerEntityId = 2001,
            Status = AgentWorkItemStatus.Pending,
            NextRunAt = AppTime.Now.AddMinutes(-1),
            IdempotencyKey = $"same-task:{Guid.NewGuid():N}",
            CauseChainId = Guid.NewGuid().ToString("N"),
            Prompt = "等待前一个工作项完成"
        };
        database.Context.AgentWorkItems.Add(second);
        await database.Context.SaveChangesAsync();

        Assert.Null(await database.Queue.ClaimNextAsync());

        await database.Context.AgentWorkItems
            .Where(item => item.Id == first.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AgentWorkItemStatus.Completed)
                .SetProperty(item => item.CompletedAt, AppTime.Now));

        Assert.Equal(second.Id, await database.Queue.ClaimNextAsync());

        // 兼容升级或恢复时出现的反向顺序：即使待执行项编号更小，
        // 只要同任务已有较新的工作项运行，也不能并发领取。
        await database.Context.AgentWorkItems
            .Where(item => item.Id == first.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AgentWorkItemStatus.Pending)
                .SetProperty(item => item.NextRunAt, AppTime.Now.AddMinutes(-1))
                .SetProperty(item => item.CompletedAt, (DateTime?)null));

        Assert.Null(await database.Queue.ClaimNextAsync());

        await database.Context.AgentWorkItems
            .Where(item => item.Id == second.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AgentWorkItemStatus.Completed)
                .SetProperty(item => item.CompletedAt, AppTime.Now));

        Assert.Equal(first.Id, await database.Queue.ClaimNextAsync());
    }

    [Fact]
    public async Task Enqueue_DisabledAgentMarksTaskFailedInsteadOfLeavingPending()
    {
        await using var database = await QueueDatabase.CreateAsync(AgentWorkItemStatus.Pending);
        var task = await database.Context.ToDoTasks
            .Include(item => item.AgentDefinition)
            .SingleAsync(item => item.Id == database.TaskId);
        task.AgentDefinition!.IsEnabled = false;
        task.AgentExecutionStatus = AgentTaskExecutionStatus.Pending;
        task.AgentLastError = string.Empty;
        await database.Context.SaveChangesAsync();

        var result = await database.Queue.EnqueueTaskAsync(
            task.Id,
            database.AdminUserId,
            AgentWorkTriggerType.TaskCommentAdded,
            1001,
            "继续执行",
            $"disabled-agent:{Guid.NewGuid():N}");

        Assert.Null(result);
        var snapshot = await database.Context.ToDoTasks.AsNoTracking()
            .SingleAsync(item => item.Id == task.Id);
        Assert.Equal(AgentTaskExecutionStatus.Failed, snapshot.AgentExecutionStatus);
        Assert.Contains("已停用", snapshot.AgentLastError);
    }

    [Fact]
    public async Task Enqueue_MissingRequesterMarksTaskFailedInsteadOfLeavingPending()
    {
        await using var database = await QueueDatabase.CreateAsync(AgentWorkItemStatus.Pending);

        var result = await database.Queue.EnqueueTaskAsync(
            database.TaskId,
            int.MaxValue,
            AgentWorkTriggerType.TaskCommentAdded,
            1002,
            "继续执行",
            $"missing-requester:{Guid.NewGuid():N}");

        Assert.Null(result);
        var snapshot = await database.Context.ToDoTasks.AsNoTracking()
            .SingleAsync(item => item.Id == database.TaskId);
        Assert.Equal(AgentTaskExecutionStatus.Failed, snapshot.AgentExecutionStatus);
        Assert.Contains("发起人不存在", snapshot.AgentLastError);
    }

    [Fact]
    public async Task RecoverStaleWorkItem_RequeuesAndSynchronizesTaskStatus()
    {
        await using var database = await QueueDatabase.CreateAsync(AgentWorkItemStatus.Running);
        await database.Context.AgentWorkItems
            .Where(item => item.Id == database.WorkItemId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LockedAt, AppTime.Now.Subtract(AgentWorkQueueService.StaleWorkItemTimeout).AddSeconds(-1))
                .SetProperty(item => item.AttemptCount, 1)
                .SetProperty(item => item.MaxAttempts, 3));

        await database.Queue.RecoverStaleWorkItemsAsync();

        var workItem = await database.Context.AgentWorkItems.AsNoTracking()
            .SingleAsync(item => item.Id == database.WorkItemId);
        Assert.Equal(AgentWorkItemStatus.Retrying, workItem.Status);
        Assert.Null(workItem.LockedAt);
        Assert.Contains("自动恢复", workItem.ErrorMessage);
        Assert.Equal(AgentTaskExecutionStatus.Retrying, await database.TaskStatusAsync());
    }

    private sealed class QueueDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private QueueDatabase(
            SqliteConnection connection,
            ApplicationDbContext context,
            AgentWorkQueueService queue,
            TestEventBus events,
            int adminUserId,
            int workItemId,
            int taskId)
        {
            _connection = connection;
            Context = context;
            Queue = queue;
            Events = events;
            AdminUserId = adminUserId;
            WorkItemId = workItemId;
            TaskId = taskId;
        }

        public ApplicationDbContext Context { get; }
        public AgentWorkQueueService Queue { get; }
        public TestEventBus Events { get; }
        public int AdminUserId { get; }
        public int WorkItemId { get; }
        public int TaskId { get; }

        public static async Task<QueueDatabase> CreateAsync(AgentWorkItemStatus initialStatus)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var context = new ApplicationDbContext(options);
            await context.Database.EnsureCreatedAsync();

            var admin = new ApplicationUser
            {
                UserName = "queue-admin",
                NormalizedUserName = "QUEUE-ADMIN",
                RealName = "队列管理员",
                Role = UserRole.systemAdmin,
                Status = UserStatus.Active
            };
            context.Users.Add(admin);
            await context.SaveChangesAsync();
            var project = new Project
            {
                Name = "队列测试项目",
                CreatedByUserId = admin.Id,
                LeaderUserId = admin.Id,
                Description = "测试",
                Requirements = "测试"
            };
            var agent = new AgentDefinition
            {
                AgentKey = "queue-test-agent",
                Name = "队列测试 Agent",
                Description = "测试",
                SystemPrompt = "测试",
                IsEnabled = true
            };
            context.AddRange(project, agent);
            await context.SaveChangesAsync();
            var task = new ToDoTask
            {
                ProjectId = project.Id,
                CreatorId = admin.Id,
                Title = "Agent队列测试任务",
                Status = TaskStatus.InProgress,
                AssigneeType = TaskAssigneeType.DigitalEmployee,
                AgentDefinitionId = agent.Id,
                AgentName = agent.Name,
                AgentExecutionStatus = initialStatus == AgentWorkItemStatus.Failed
                    ? AgentTaskExecutionStatus.Failed
                    : initialStatus == AgentWorkItemStatus.WaitingApproval
                        ? AgentTaskExecutionStatus.WaitingApproval
                    : AgentTaskExecutionStatus.Pending,
                AgentAssignmentVersion = 1
            };
            context.ToDoTasks.Add(task);
            await context.SaveChangesAsync();
            var workItem = new AgentWorkItem
            {
                AgentDefinitionId = agent.Id,
                ProjectId = project.Id,
                TaskId = task.Id,
                RequestedByUserId = admin.Id,
                TriggerType = AgentWorkTriggerType.TaskAssigned,
                TriggerEntityId = task.Id,
                Status = initialStatus,
                IdempotencyKey = $"queue-test:{Guid.NewGuid():N}",
                CauseChainId = Guid.NewGuid().ToString("N"),
                Prompt = "执行测试任务",
                AttemptCount = initialStatus == AgentWorkItemStatus.Failed ? 3 : 0,
                CompletedAt = initialStatus == AgentWorkItemStatus.Failed ? DateTime.Now : null
            };
            context.AgentWorkItems.Add(workItem);
            await context.SaveChangesAsync();

            var events = new TestEventBus();
            var queue = new AgentWorkQueueService(
                context,
                null!,
                null!,
                null!,
                events,
                new AgentOutcomeService(context),
                NullLogger<AgentWorkQueueService>.Instance);
            return new QueueDatabase(connection, context, queue, events, admin.Id, workItem.Id, task.Id);
        }

        public Task<AgentWorkItemStatus> WorkItemStatusAsync() => Context.AgentWorkItems.AsNoTracking()
            .Where(item => item.Id == WorkItemId)
            .Select(item => item.Status)
            .SingleAsync();

        public Task<AgentTaskExecutionStatus> TaskStatusAsync() => Context.ToDoTasks.AsNoTracking()
            .Where(item => item.Id == TaskId)
            .Select(item => item.AgentExecutionStatus)
            .SingleAsync();

        public async Task<int> AttachToolCallAsync(AgentToolCallStatus status)
        {
            var workItem = await Context.AgentWorkItems.FirstAsync(item => item.Id == WorkItemId);
            var session = new AiSession
            {
                AgentKey = "queue-test-agent",
                UserId = AdminUserId,
                ProjectId = workItem.ProjectId,
                TaskId = TaskId,
                Prompt = "测试审批续跑",
                Status = AiSessionStatus.WaitingHuman,
                LastActivityAt = DateTime.Now
            };
            Context.AiSessions.Add(session);
            await Context.SaveChangesAsync();
            workItem.AiSessionId = session.Id;
            Context.AgentToolCalls.Add(new AgentToolCall
            {
                AiSessionId = session.Id,
                ToolName = "task.update",
                ArgumentsJson = "{}",
                ResultJson = "{}",
                RequiresApproval = true,
                Status = status,
                CompletedAt = DateTime.Now
            });
            await Context.SaveChangesAsync();
            return session.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    public sealed class TestEventBus : IEventBus
    {
        public List<EventBusMessage> Messages { get; } = [];

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
            Messages.Add(message);
            return Task.FromResult(message);
        }

        public Task<List<EventBusMessage>> GetRecentAsync(int take = 100, CancellationToken cancellationToken = default)
            => Task.FromResult(Messages.Take(take).ToList());
    }
}

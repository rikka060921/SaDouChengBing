using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;
using TaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Test;

public sealed class MeetingAgentReviewTests
{
    [Theory]
    [InlineData(true, AgentWorkItemStatus.Pending)]
    [InlineData(true, AgentWorkItemStatus.Running)]
    [InlineData(true, AgentWorkItemStatus.WaitingApproval)]
    [InlineData(true, AgentWorkItemStatus.Retrying)]
    [InlineData(true, AgentWorkItemStatus.Paused)]
    [InlineData(false, AgentWorkItemStatus.Pending)]
    [InlineData(false, AgentWorkItemStatus.Running)]
    [InlineData(false, AgentWorkItemStatus.WaitingApproval)]
    [InlineData(false, AgentWorkItemStatus.Retrying)]
    [InlineData(false, AgentWorkItemStatus.Paused)]
    public async Task Confirm_TaskChange_ReviewsDeliveryAndQueueAtomically_AndIsIdempotent(bool approved, AgentWorkItemStatus oldWorkStatus)
    {
        await using var db = await AgentOutcomeServiceTests.OutcomeDatabase.CreateAsync();
        var (meeting, agenda, receipt) = await PrepareAsync(db, approved);
        var oldWork = new AgentWorkItem
        {
            AgentDefinitionId = db.Agent.Id, TaskId = db.Task.Id, ProjectId = db.Project.Id,
            RequestedByUserId = db.Admin.Id, IdempotencyKey = "stale-work", Status = oldWorkStatus
        };
        db.Context.AgentWorkItems.Add(oldWork);
        await db.Context.SaveChangesAsync();
        var service = CreateService(db.Context);
        Assert.True((await service.ConfirmAsync(meeting.Id, db.Admin.Id)).Success);
        var commentCount = await db.Context.TaskComments.CountAsync();
        var logCount = await db.Context.ChangeLogs.CountAsync();
        var notificationCount = await db.Context.UserNotifications.CountAsync();
        Assert.True((await service.ConfirmAsync(meeting.Id, db.Admin.Id)).Success);
        db.Context.ChangeTracker.Clear();

        var task = await db.Context.ToDoTasks.SingleAsync(t => t.Id == db.Task.Id);
        var delivery = await db.Context.AgentDeliveryReceipts.SingleAsync(r => r.Id == receipt.Id);
        var savedAgenda = await db.Context.MeetingAgendas.SingleAsync(a => a.Id == agenda.Id);
        Assert.Equal(approved ? TaskStatus.Completed : TaskStatus.InProgress, task.Status);
        Assert.Equal(approved, task.IsCompleted);
        Assert.Equal(approved ? AgentTaskExecutionStatus.Completed : AgentTaskExecutionStatus.Pending, task.AgentExecutionStatus);
        Assert.Equal(approved ? AgentDeliveryAcceptanceStatus.Accepted : AgentDeliveryAcceptanceStatus.Rejected, delivery.AcceptanceStatus);
        Assert.Equal(db.Admin.Id, delivery.ReviewedByUserId);
        Assert.Equal(approved ? 0 : 1, task.ReworkCount);
        Assert.Equal(AgentWorkItemStatus.Cancelled, (await db.Context.AgentWorkItems.SingleAsync(w => w.Id == oldWork.Id)).Status);
        Assert.Equal(approved ? AgendaStatus.Archived : AgendaStatus.Active, savedAgenda.Status);
        Assert.Equal(approved ? meeting.Id : (int?)null, savedAgenda.ArchivedByMeetingId);
        Assert.Equal(approved ? 1 : 0, await db.Context.MeetingAgendaRelations.CountAsync(r => r.MeetingAgendaId == agenda.Id));
        Assert.Equal(commentCount, await db.Context.TaskComments.CountAsync());
        Assert.Equal(logCount, await db.Context.ChangeLogs.CountAsync());
        Assert.Equal(notificationCount, await db.Context.UserNotifications.CountAsync());
        Assert.Single(await db.Context.AgentPerformanceSignals.Where(s => s.EventType == (approved ? AgentPerformanceEventType.HumanAccepted : AgentPerformanceEventType.HumanRejected)).ToListAsync());
        var rework = await db.Context.AgentWorkItems.Where(w => w.TriggerType == AgentWorkTriggerType.TaskReviewRejected).ToListAsync();
        if (approved) Assert.Empty(rework);
        else
        {
            var queued = Assert.Single(rework);
            Assert.Equal(AgentWorkItemStatus.Pending, queued.Status);
            Assert.Equal(db.Admin.Id, queued.RequestedByUserId);
            Assert.Contains("补充测试证据", queued.Prompt);
            Assert.True(await db.Context.TaskComments.AnyAsync(c => c.Id == queued.TriggerEntityId && c.TaskId == task.Id));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Confirm_ReviewThenAuditFailure_RollsBackAllReviewSideEffects(bool approved)
    {
        await using var db = await AgentOutcomeServiceTests.OutcomeDatabase.CreateAsync();
        var (meeting, agenda, receipt) = await PrepareAsync(db, approved);
        var service = CreateService(db.Context, new FailingAudit(db.Context));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(meeting.Id, db.Admin.Id));
        db.Context.ChangeTracker.Clear();

        var task = await db.Context.ToDoTasks.SingleAsync(t => t.Id == db.Task.Id);
        Assert.Equal(TaskStatus.PendingConfirmation, task.Status);
        Assert.Equal(AgentTaskExecutionStatus.AwaitingConfirmation, task.AgentExecutionStatus);
        Assert.Equal(0, task.ReworkCount);
        Assert.Equal(AgentDeliveryAcceptanceStatus.PendingReview, (await db.Context.AgentDeliveryReceipts.SingleAsync(r => r.Id == receipt.Id)).AcceptanceStatus);
        Assert.Equal(AgendaStatus.Active, (await db.Context.MeetingAgendas.SingleAsync(a => a.Id == agenda.Id)).Status);
        Assert.Empty(await db.Context.MeetingAgendaRelations.ToListAsync());
        Assert.Single(await db.Context.AgentWorkItems.ToListAsync());
        Assert.Empty(await db.Context.TaskComments.ToListAsync());
        Assert.Empty(await db.Context.ChangeLogs.ToListAsync());
        Assert.Empty(await db.Context.UserNotifications.ToListAsync());
        Assert.Single(await db.Context.AgentPerformanceSignals.ToListAsync());
        Assert.Null((await db.Context.MeetingMinutes.SingleAsync(m => m.Id == meeting.Id)).ConfirmedAt);
        Assert.False((await db.Context.MeetingActionItems.SingleAsync()).IsConfirmed);
    }

    [Fact]
    public async Task Confirm_RejectedWithDisabledAgent_ShowsQueueFailureInsteadOfPhantomPendingWork()
    {
        await using var db = await AgentOutcomeServiceTests.OutcomeDatabase.CreateAsync();
        var (meeting, _, _) = await PrepareAsync(db, false);
        db.Agent.IsEnabled = false;
        await db.Context.SaveChangesAsync();
        Assert.True((await CreateService(db.Context).ConfirmAsync(meeting.Id, db.Admin.Id)).Success);
        db.Context.ChangeTracker.Clear();
        var task = await db.Context.ToDoTasks.SingleAsync();
        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.Equal(AgentTaskExecutionStatus.Failed, task.AgentExecutionStatus);
        Assert.Contains("已停用", task.AgentLastError);
        Assert.Single(await db.Context.AgentWorkItems.ToListAsync());
    }

    [Theory]
    [InlineData("PendingConfirmation", "Completed", TaskStatus.InProgress, "状态已变化")]
    [InlineData("InProgress", "Completed", TaskStatus.InProgress, "尚未提交待验收结果")]
    [InlineData("PendingConfirmation", "InvalidStatus", TaskStatus.PendingConfirmation, "状态无效")]
    public async Task Confirm_StaleOrPrematureApproval_DoesNotOverwriteTask(string before, string after, TaskStatus current, string message)
    {
        await using var db = await AgentOutcomeServiceTests.OutcomeDatabase.CreateAsync();
        var (meeting, _, _) = await PrepareAsync(db, true);
        db.Task.SetStatus(current);
        var change = await db.Context.MeetingActionItems.SingleAsync();
        change.BeforeStatus = before;
        change.AfterStatus = after;
        await db.Context.SaveChangesAsync();
        var result = await CreateService(db.Context).ConfirmAsync(meeting.Id, db.Admin.Id);
        Assert.False(result.Success);
        Assert.Contains(message, result.ErrorMessage);
        db.Context.ChangeTracker.Clear();
        Assert.Equal(current, (await db.Context.ToDoTasks.SingleAsync()).Status);
        Assert.Equal(AgentDeliveryAcceptanceStatus.PendingReview, (await db.Context.AgentDeliveryReceipts.SingleAsync()).AcceptanceStatus);
        Assert.False((await db.Context.MeetingActionItems.SingleAsync()).IsConfirmed);
        Assert.Null((await db.Context.MeetingMinutes.SingleAsync()).ConfirmedAt);
        Assert.Empty(await db.Context.TaskComments.ToListAsync());
    }

    private static async Task<(MeetingMinutes Meeting, MeetingAgenda Agenda, AgentDeliveryReceipt Receipt)> PrepareAsync(
        AgentOutcomeServiceTests.OutcomeDatabase db, bool approved)
    {
        var receipt = await new AgentOutcomeService(db.Context).CreateDeliveryAsync(db.WorkItem, db.Task, db.Session, "等待验收");
        var meeting = new MeetingMinutes { MeetingTitle = "Agent 验收会议", MeetingContent = "验收决议", MeetingDate = AppTime.Today, CreatorId = db.Admin.Id, ProjectId = db.Project.Id, IsDraft = false };
        var agenda = new MeetingAgenda { Title = db.Task.Title, ProjectId = db.Project.Id, SourceId = db.Task.Id, Status = AgendaStatus.Active };
        db.Context.MeetingMinutes.Add(meeting);
        db.Context.MeetingAgendas.Add(agenda);
        await db.Context.SaveChangesAsync();
        db.Context.MeetingActionItems.Add(new MeetingActionItem
        {
            MeetingMinutesId = meeting.Id, Title = db.Task.Title, Content = db.Task.Title,
            ActionType = "TaskChange", MatchedTaskId = db.Task.Id, MeetingAgendaId = agenda.Id, SyncStatus = "待确认更新",
            BeforeStatus = "PendingConfirmation", AfterStatus = approved ? "Completed" : "InProgress",
            BeforeAssigneeId = db.Task.AssigneeId, AfterAssigneeId = db.Task.AssigneeId,
            BeforePriority = db.Task.Priority.ToString(), AfterPriority = db.Task.Priority.ToString(),
            ChangeDescription = approved ? "测试证据通过" : "补充测试证据"
        });
        await db.Context.SaveChangesAsync();
        return (meeting, agenda, receipt);
    }

    private static MeetingTaskSyncService CreateService(ApplicationDbContext context, ToDoTaskDomainService? taskDomain = null)
    {
        var notifications = new UserNotificationService(context);
        return new(context, DispatchProxy.Create<IAIService, NoExternalAI>(), notifications,
            taskDomain ?? new ToDoTaskDomainService(context, null!, NullLogger<ProjectDomain>.Instance),
            new AgentWorkQueueService(context, null!, null!, notifications, null!, new AgentOutcomeService(context), NullLogger<AgentWorkQueueService>.Instance));
    }

    public class NoExternalAI : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("本地测试禁止调用外部 AI");
    }

    private sealed class FailingAudit(ApplicationDbContext context) : ToDoTaskDomainService(context, null!, NullLogger<ProjectDomain>.Instance)
    {
        public override Task LogTaskOperationAsync(OperationType operationType, OperationTarget target, int operatorUserId,
            string? beforeState = null, string? afterState = null, OperationStatus status = OperationStatus.成功,
            int? projectId = null, string? projectName = null, int? taskId = null, string? taskTitle = null,
            string? taskName = null, int? targetId = null, string? targetName = null)
            => throw new InvalidOperationException("模拟审计写入失败");
    }
}

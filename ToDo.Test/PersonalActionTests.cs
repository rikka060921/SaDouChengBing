using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;
using TaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Test;

public sealed class PersonalActionTests
{
    private static readonly DateTime Now = new(2026, 9, 18, 10, 0, 0);

    [Fact]
    public void Merge_UsesUrgencyDeadlineAgeAndStableKey_AndDeduplicatesTask()
    {
        PersonalActionSignal Signal(string key, DateTime? deadline, bool critical = false, string reason = "待处理")
            => new(key, key, "项目", reason, "审核人", Now.AddDays(-1), deadline, critical, new("处理", $"/Tasks/Details/{key}"));
        var cards = PersonalActionService.Merge([
            Signal("normal", null), Signal("today", Now.AddHours(3)), Signal("overdue", Now.AddHours(-1)),
            Signal("critical", Now.AddDays(2), true), Signal("critical", Now.AddDays(2), false, "会议也受阻")], Now);
        Assert.Equal(new[] { "critical", "overdue", "today", "normal" }, cards.Select(c => c.Key));
        Assert.Equal(2, cards[0].Reasons.Count);
        Assert.Single(cards[0].Actions);
        Assert.Single(cards[0].WhyMe);
        Assert.Equal(3, cards[^1].Priority);
    }

    [Fact]
    public async Task HumanReviewAndUnconfirmedMeeting_AppearWithActionableLinks()
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(TaskStatus.PendingConfirmation);
        var meeting = await db.MeetingAsync(task, confirmed: false);
        var ownerCards = await db.Actions.GetAsync(db.Owner, Now);
        Assert.Empty(ownerCards);
        var reviewerCards = await db.Actions.GetAsync(db.Reviewer, Now);
        Assert.Equal($"task:{task.Id}", Assert.Single(reviewerCards).Key);
        var leaderCards = await db.Actions.GetAsync(db.Leader, Now);
        Assert.Equal(2, leaderCards.Count);
        Assert.Contains(leaderCards, c => c.Key == $"meeting:{meeting.MeetingMinutesId}"
            && c.Actions.Single().Url == $"/MeetingMinutes/Details/{meeting.MeetingMinutesId}");
    }

    [Fact]
    public async Task FailedMeetingAndDeadline_AreOneTask_NotThreeTodos()
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(deadline: Now.AddHours(-2), agent: true);
        task.AgentExecutionStatus = AgentTaskExecutionStatus.Failed;
        await db.WorkAsync(task, AgentWorkItemStatus.Failed);
        await db.MeetingAsync(task);
        var card = Assert.Single(await db.Actions.GetAsync(db.Leader, Now));
        Assert.Equal($"task:{task.Id}", card.Key);
        Assert.Equal(0, card.Priority);
        Assert.True(card.Reasons.Count >= 2);
        Assert.Equal(2, card.Actions.Count);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("deleted")]
    [InlineData("reassigned")]
    [InlineData("new-success")]
    [InlineData("new-pending")]
    [InlineData("retrying")]
    public async Task ObsoleteFailures_DoNotReturn(string situation)
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(agent: true);
        task.AgentExecutionStatus = AgentTaskExecutionStatus.Failed;
        await db.WorkAsync(task, AgentWorkItemStatus.Failed);
        if (situation == "completed") task.SetStatus(TaskStatus.Completed);
        if (situation == "cancelled") task.SetStatus(TaskStatus.Cancelled);
        if (situation == "deleted") task.IsDeleted = true;
        if (situation == "reassigned") { task.AssigneeType = TaskAssigneeType.Human; task.AgentDefinitionId = null; }
        if (situation == "new-success") await db.WorkAsync(task, AgentWorkItemStatus.Completed);
        if (situation == "new-pending") await db.WorkAsync(task, AgentWorkItemStatus.Pending);
        if (situation == "retrying") task.AgentExecutionStatus = AgentTaskExecutionStatus.Retrying;
        await db.Context.SaveChangesAsync();
        Assert.Empty(await db.Actions.GetAsync(db.Leader, Now));
    }

    [Fact]
    public async Task RemovedMember_CannotSeeOldDispatchReviewOrFailure()
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(agent: true);
        task.CreatorId = db.Owner.Id;
        task.ReviewerId = db.Owner.Id;
        task.AgentExecutionStatus = AgentTaskExecutionStatus.Failed;
        await db.WorkAsync(task, AgentWorkItemStatus.Failed);
        db.Context.AgentDispatchDecisions.Add(new AgentDispatchDecision
        {
            TaskId = task.Id, ProjectId = task.ProjectId, DispatchVersion = task.AgentAssignmentVersion,
            Status = AgentDispatchDecisionStatus.NoCandidate, RequestedByUserId = db.Owner.Id
        });
        db.Context.ProjectUsers.Remove(await db.Context.ProjectUsers.SingleAsync(m => m.UserId == db.Owner.Id));
        await db.Context.SaveChangesAsync();
        Assert.Empty(await db.Actions.GetAsync(db.Owner, Now));
        Assert.DoesNotContain(db.Owner.Id, await new AttentionRecipientService(db.Context).TaskAsync(task));
    }

    [Fact]
    public async Task NoEarlyCategoryLimit_AndSourceCompletionRemovesCard()
    {
        await using var db = await Fixture.CreateAsync();
        for (var i = 0; i < 56; i++) await db.TaskAsync(TaskStatus.PendingConfirmation, Now.AddDays(i + 1));
        var urgent = await db.TaskAsync(TaskStatus.PendingConfirmation, Now.AddDays(-1));
        var cards = await db.Actions.GetAsync(db.Reviewer, Now);
        Assert.Equal(57, cards.Count);
        Assert.Equal($"task:{urgent.Id}", cards[0].Key);
        urgent.SetStatus(TaskStatus.Completed);
        await db.Context.SaveChangesAsync();
        Assert.Equal(56, (await db.Actions.GetAsync(db.Reviewer, Now)).Count);
    }

    [Fact]
    public async Task MeetingStatus_IsRecomputedFromLiveDeadline_AndSnoozeIsRespected()
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(deadline: Now.AddDays(5));
        var action = await db.MeetingAsync(task);
        action.Deadline = Now.AddDays(-2);
        action.SupervisionStatus = MeetingActionSupervisionStatus.Overdue;
        await db.Context.SaveChangesAsync();
        Assert.Empty(await db.Actions.GetAsync(db.Owner, Now));
        task.EndTime = Now.AddHours(-2);
        action.SnoozedUntil = Now.AddDays(1);
        await db.Context.SaveChangesAsync();
        Assert.Empty(await db.Actions.GetAsync(db.Owner, Now));
        await new UserNotificationService(db.Context).GenerateTaskRemindersAsync(Now);
        Assert.Empty(await db.Context.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task WorkingAgent_IsNotHumanTodo_AndHasNoEarlyDeadlineAlert()
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(deadline: Now.AddHours(3), agent: true);
        task.AgentExecutionStatus = AgentTaskExecutionStatus.Running;
        await db.MeetingAsync(task);
        Assert.Empty(await db.Actions.GetAsync(db.Leader, Now));
        await new MeetingActionSupervisionService(db.Context).RunAsync(Now);
        await new UserNotificationService(db.Context).GenerateTaskRemindersAsync(Now);
        Assert.Empty(await db.Context.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task Reminders_GoToOwnerThenEscalateToOneManager_OncePerDay()
    {
        await using var db = await Fixture.CreateAsync();
        await db.TaskAsync(deadline: Now.AddHours(2));
        var service = new UserNotificationService(db.Context);
        await service.GenerateTaskRemindersAsync(Now);
        await service.GenerateTaskRemindersAsync(Now.AddMinutes(10));
        Assert.Equal(db.Owner.Id, (await db.Context.UserNotifications.SingleAsync()).UserId);
        await service.GenerateTaskRemindersAsync(Now.AddDays(2));
        var escalated = await db.Context.UserNotifications.Where(n => n.CreatedAt >= Now.AddDays(2).Date).ToListAsync();
        Assert.Equal(new[] { db.Leader.Id, db.Owner.Id }.Order(), escalated.Select(n => n.UserId).Order());
    }

    [Fact]
    public async Task MultipleMeetingActionsAndReminderSweep_ProduceOneOwnerAlert()
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(deadline: Now.AddHours(2));
        await db.MeetingAsync(task);
        await db.MeetingAsync(task);
        await new UserNotificationService(db.Context).GenerateTaskRemindersAsync(Now);
        await new MeetingActionSupervisionService(db.Context).RunAsync(Now);
        Assert.Equal(db.Owner.Id, (await db.Context.UserNotifications.SingleAsync()).UserId);
    }

    [Fact]
    public async Task DeletedMeetingTask_IsAuditedAsCancelled_WithoutReminder()
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(deadline: Now.AddDays(-3));
        var action = await db.MeetingAsync(task);
        task.IsDeleted = true;
        await db.Context.SaveChangesAsync();
        await new MeetingActionSupervisionService(db.Context).RunAsync(Now);
        Assert.Equal(MeetingActionSupervisionStatus.Cancelled, action.SupervisionStatus);
        Assert.Empty(await db.Context.UserNotifications.ToListAsync());
        Assert.Empty(await db.Actions.GetAsync(db.Leader, Now));
    }

    [Fact]
    public async Task Notifications_KeepQuietHistory_WithoutUnreadBurden()
    {
        await using var db = await Fixture.CreateAsync();
        var service = new UserNotificationService(db.Context);
        foreach (var type in new[] { "Success", "Info", "Task" })
            await service.NotifyAsync(db.Owner.Id, "普通进展", "结果可追溯", type);
        await service.NotifyAsync(db.Owner.Id, "需要你确认", "等待处理", "Review");
        Assert.Single(await service.GetForUserAsync(db.Owner.Id, true));
        Assert.Equal(4, (await service.GetForUserAsync(db.Owner.Id)).Count);
        await service.MarkAllReadAsync(db.Owner.Id);
        Assert.Empty(await service.GetForUserAsync(db.Owner.Id, true));
    }

    [Fact]
    public async Task ReviewerNotification_IsSingleAndCannotSelfReview()
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(TaskStatus.PendingConfirmation);
        var recipients = new AttentionRecipientService(db.Context);
        Assert.Equal(db.Reviewer.Id, Assert.Single(await recipients.TaskAsync(task)));
        task.CreatorId = db.Reviewer.Id;
        await db.Context.SaveChangesAsync();
        Assert.Equal(db.Leader.Id, Assert.Single(await recipients.TaskAsync(task)));
    }

    [Fact]
    public async Task HighRiskApproval_NotifiesOneEligibleIndependentApprover()
    {
        await using var db = await Fixture.CreateAsync();
        var request = await db.Approvals.RequestAsync(db.Project.Id, db.Leader.Id, "AgentToolCall", 77,
            "test", "需要独立审批", "{}", riskLevel: AgentRiskLevel.High);
        Assert.Equal(db.Admin.Id, (await db.Context.UserNotifications.SingleAsync()).UserId);
        Assert.Empty(await db.Actions.GetAsync(db.Leader, Now));
        var card = Assert.Single(await db.Actions.GetAsync(db.Admin, Now));
        Assert.Equal(0, card.Priority);
        Assert.Contains($"requestId={request.Id}", card.Actions.Single().Url);
        Assert.Empty(await db.Approvals.GetAccessibleAsync(db.Owner, requestId: request.Id));
        Assert.Single(await db.Approvals.GetAccessibleAsync(db.Admin, requestId: request.Id));
    }

    [Fact]
    public async Task AiReview_IsNotPresentedAsHumanApproval()
    {
        await using var db = await Fixture.CreateAsync();
        await db.Approvals.RequestAsync(db.Project.Id, db.Owner.Id, "AgentToolCall", 88, "test", "AI 审核中", "{}",
            riskLevel: AgentRiskLevel.Low, reviewMode: AgentToolReviewMode.AiReview, notifyApprovers: false);
        Assert.Empty(await db.Actions.GetAsync(db.Leader, Now));
    }

    [Fact]
    public async Task ReworkNotificationAndTodo_TargetActualHumanOwner()
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(TaskStatus.PendingConfirmation, Now.AddDays(4));
        await new TaskReviewService(db.Context).StageReviewAsync(task, db.Reviewer, false, "请补齐来源");
        await db.Context.SaveChangesAsync();
        Assert.Equal(db.Owner.Id, (await db.Context.UserNotifications.SingleAsync()).UserId);
        Assert.Contains("返工", Assert.Single(await db.Actions.GetAsync(db.Owner, Now)).Reasons.Single());
    }

    [Fact]
    public async Task MissingDefinition_NotifiesManager_NotAnOwnerWhoCannotEditMeeting()
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync();
        await db.MeetingAsync(task);
        await new MeetingActionSupervisionService(db.Context).RunAsync(Now);
        Assert.Equal(db.Leader.Id, (await db.Context.UserNotifications.SingleAsync()).UserId);
        Assert.Empty(await db.Actions.GetAsync(db.Owner, Now));
        Assert.Single(await db.Actions.GetAsync(db.Leader, Now));
    }

    [Fact]
    public async Task PendingPlanAndDispatch_AreMerged_AndOnlyCurrentVersionsAppear()
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(agent: true);
        await db.WorkAsync(task, AgentWorkItemStatus.WaitingPlanConfirmation);
        db.Context.AgentDispatchDecisions.Add(new AgentDispatchDecision
        {
            TaskId = task.Id, ProjectId = task.ProjectId, RequestedByUserId = db.Leader.Id,
            DispatchVersion = 0, Status = AgentDispatchDecisionStatus.NoCandidate
        });
        await db.Context.SaveChangesAsync();
        var card = Assert.Single(await db.Actions.GetAsync(db.Leader, Now));
        Assert.Single(card.Reasons);
        Assert.Contains("#execution-plan", card.Actions.Single().Url);
        var decision = await db.Context.AgentDispatchDecisions.SingleAsync();
        decision.DispatchVersion = task.AgentAssignmentVersion;
        await db.Context.SaveChangesAsync();
        card = Assert.Single(await db.Actions.GetAsync(db.Leader, Now));
        Assert.Equal(2, card.Reasons.Count);
    }

    [Theory]
    [InlineData(AgentTaskExecutionStatus.AwaitingPlanConfirmation)]
    [InlineData(AgentTaskExecutionStatus.Pending)]
    [InlineData(AgentTaskExecutionStatus.Running)]
    public async Task WaitingPlan_WithLaterQueuedWork_RemainsOneActionForEligibleReviewers(AgentTaskExecutionStatus aggregateStatus)
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(agent: true);
        var plan = await db.WorkAsync(task, AgentWorkItemStatus.WaitingPlanConfirmation);
        await db.WorkAsync(task, AgentWorkItemStatus.Pending);
        await db.WorkAsync(task, AgentWorkItemStatus.Pending);
        // 队列工作状态才是阻塞事实；领取/恢复期间任务上的聚合状态可能暂时滞后。
        task.AgentExecutionStatus = aggregateStatus;
        await db.Context.SaveChangesAsync();

        foreach (var reviewer in new[] { db.Leader, db.Reviewer })
        {
            var card = Assert.Single(await db.Actions.GetAsync(reviewer, Now));
            Assert.Equal($"task:{task.Id}", card.Key);
            Assert.Equal(plan.CreatedAt, card.CreatedAt);
            Assert.Equal("执行计划需要你确认后才能继续。", Assert.Single(card.Reasons));
            Assert.Equal($"/Tasks/Details/{task.Id}#execution-plan", Assert.Single(card.Actions).Url);
        }
        Assert.Empty(await db.Actions.GetAsync(db.Owner, Now));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("deleted")]
    [InlineData("reassigned-human")]
    [InlineData("reassigned-agent")]
    [InlineData("plan-cancelled")]
    [InlineData("plan-confirmed")]
    [InlineData("plan-completed")]
    [InlineData("member-removed")]
    public async Task WaitingPlan_WithLaterQueuedWork_DoesNotReviveObsoleteOrInaccessiblePlans(string situation)
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(agent: true);
        var plan = await db.WorkAsync(task, AgentWorkItemStatus.WaitingPlanConfirmation);
        await db.WorkAsync(task, AgentWorkItemStatus.Pending);
        if (situation == "completed") task.SetStatus(TaskStatus.Completed);
        if (situation == "cancelled") task.SetStatus(TaskStatus.Cancelled);
        if (situation == "deleted") task.IsDeleted = true;
        if (situation == "reassigned-human") { task.AssigneeType = TaskAssigneeType.Human; task.AgentDefinitionId = null; }
        if (situation == "reassigned-agent")
        {
            var replacement = new AgentDefinition { AgentKey = "replacement", Name = "接手助手", SystemPrompt = "只读分析" };
            db.Context.Add(replacement);
            await db.Context.SaveChangesAsync();
            task.AgentDefinitionId = replacement.Id;
        }
        if (situation == "plan-cancelled") plan.Status = AgentWorkItemStatus.Cancelled;
        if (situation == "plan-confirmed")
        {
            plan.Status = AgentWorkItemStatus.Pending;
            plan.PlanApprovedAt = Now;
            task.AgentExecutionStatus = AgentTaskExecutionStatus.Pending;
        }
        if (situation == "plan-completed") plan.Status = AgentWorkItemStatus.Completed;
        if (situation == "member-removed")
            db.Context.ProjectUsers.Remove(await db.Context.ProjectUsers.SingleAsync(m => m.UserId == db.Reviewer.Id));
        await db.Context.SaveChangesAsync();

        Assert.Empty(await db.Actions.GetAsync(db.Reviewer, Now));
    }

    [Fact]
    public async Task WaitingPlan_WithLaterQueuedWork_MergesMeetingAndApprovalIntoTheSameTask()
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(agent: true);
        await db.WorkAsync(task, AgentWorkItemStatus.WaitingPlanConfirmation);
        await db.WorkAsync(task, AgentWorkItemStatus.Pending);
        var meeting = await db.MeetingAsync(task);
        var request = await db.Approvals.RequestAsync(db.Project.Id, db.Owner.Id, "AgentToolCall", 89,
            "test", "写入操作需要授权", "{}", notifyApprovers: false);
        var session = new AiSession { AgentKey = db.Agent.AgentKey, UserId = db.Owner.Id, ProjectId = db.Project.Id, TaskId = task.Id };
        db.Context.Add(session);
        await db.Context.SaveChangesAsync();
        db.Context.AgentToolCalls.Add(new AgentToolCall
        {
            AiSessionId = session.Id, ApprovalRequestId = request.Id, ToolName = "test",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Status = AgentToolCallStatus.PendingApproval
        });
        await db.Context.SaveChangesAsync();

        var card = Assert.Single(await db.Actions.GetAsync(db.Leader, Now));
        Assert.Equal($"task:{task.Id}", card.Key);
        Assert.Equal(3, card.Reasons.Count);
        Assert.Equal(3, card.Actions.Count);
        Assert.Contains(card.Actions, action => action.Url == $"/Tasks/Details/{task.Id}#execution-plan");
        Assert.Contains(card.Actions, action => action.Url == $"/MeetingMinutes/Details/{meeting.MeetingMinutesId}");
        Assert.Contains(card.Actions, action => action.Url == $"/Approvals/Index?requestId={request.Id}#approval-{request.Id}");
    }

    [Fact]
    public async Task InactiveReviewer_FallsBackToActiveManager()
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(TaskStatus.PendingConfirmation);
        db.Reviewer.Status = UserStatus.Locked;
        await db.Context.SaveChangesAsync();
        Assert.Empty(await db.Actions.GetAsync(db.Reviewer, Now));
        Assert.Equal(db.Leader.Id, Assert.Single(await new AttentionRecipientService(db.Context).TaskAsync(task)));
    }

    [Fact]
    public async Task WaitingApproval_DoesNotAlsoRemindApplicantToUnblockMeeting()
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(deadline: Now.AddDays(-1), agent: true);
        task.AgentExecutionStatus = AgentTaskExecutionStatus.WaitingApproval;
        await db.MeetingAsync(task);
        Assert.Empty(await db.Actions.GetAsync(db.Leader, Now));
        await new MeetingActionSupervisionService(db.Context).RunAsync(Now);
        await new UserNotificationService(db.Context).GenerateTaskRemindersAsync(Now);
        Assert.Empty(await db.Context.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task LongFailureReason_DoesNotExceedNotificationStorageLimit()
    {
        await using var db = await Fixture.CreateAsync();
        var task = await db.TaskAsync(agent: true);
        await new UserNotificationService(db.Context).StageTaskAttentionAsync(task, "Blocked", new string('题', 300),
            new string('错', 2300), Now);
        await db.Context.SaveChangesAsync();
        var notice = await db.Context.UserNotifications.SingleAsync();
        Assert.Equal(200, notice.Title.Length);
        Assert.Equal(2000, notice.Content.Length);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public ApplicationDbContext Context { get; }
        public ApplicationUser Leader { get; } = User("leader");
        public ApplicationUser Owner { get; } = User("owner");
        public ApplicationUser Reviewer { get; } = User("reviewer");
        public ApplicationUser Admin { get; } = User("admin");
        public Project Project { get; private set; } = null!;
        public AgentDefinition Agent { get; } = new() { AgentKey = "personal-test", Name = "测试助手", SystemPrompt = "只读分析" };
        public ApprovalRequestService Approvals => new(Context, null!, new UserNotificationService(Context), null!);
        public PersonalActionService Actions => new(Context, Approvals);
        private Fixture(SqliteConnection connection, ApplicationDbContext context) { this.connection = connection; Context = context; }
        private static ApplicationUser User(string name) => new() { UserName = name, NormalizedUserName = name.ToUpperInvariant() };
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var db = new Fixture(connection, context);
            context.AddRange(db.Leader, db.Owner, db.Reviewer, db.Admin, db.Agent);
            await context.SaveChangesAsync();
            db.Project = new Project { Name = "待办测试项目", LeaderUserId = db.Leader.Id, CreatedByUserId = db.Leader.Id };
            context.Add(db.Project);
            await context.SaveChangesAsync();
            context.ProjectUsers.AddRange(new[] { db.Owner, db.Reviewer, db.Admin }.Select(u => new ProjectUser
            {
                ProjectId = db.Project.Id, UserId = u.Id,
                ProjectRole = u == db.Admin ? (int)ProjectRole.Admin : (int)ProjectRole.Member
            }));
            await context.SaveChangesAsync();
            return db;
        }
        public async Task<ToDoTask> TaskAsync(TaskStatus status = TaskStatus.InProgress, DateTime? deadline = null, bool agent = false)
        {
            var task = new ToDoTask
            {
                Title = "待处理测试任务", CreatorId = Leader.Id, ProjectId = Project.Id, ReviewerId = Reviewer.Id,
                AssigneeId = agent ? null : Owner.Id, AssigneeType = agent ? TaskAssigneeType.DigitalEmployee : TaskAssigneeType.Human,
                AgentDefinitionId = agent ? Agent.Id : null, AgentAssignmentVersion = 1,
                Status = status, EndTime = deadline, CreatedAt = Now.AddDays(-2), UpdatedAt = Now
            };
            Context.Add(task); await Context.SaveChangesAsync(); return task;
        }
        public async Task<AgentWorkItem> WorkAsync(ToDoTask task, AgentWorkItemStatus status)
        {
            if (status == AgentWorkItemStatus.WaitingPlanConfirmation)
                task.AgentExecutionStatus = AgentTaskExecutionStatus.AwaitingPlanConfirmation;
            var work = new AgentWorkItem
            {
                TaskId = task.Id, ProjectId = Project.Id, AgentDefinitionId = Agent.Id,
                RequestedByUserId = Leader.Id, Status = status, IdempotencyKey = Guid.NewGuid().ToString(),
                ErrorMessage = "资料缺失", CreatedAt = Now.AddHours(-2), UpdatedAt = Now
            };
            Context.Add(work); await Context.SaveChangesAsync(); return work;
        }
        public async Task<MeetingActionItem> MeetingAsync(ToDoTask task, bool confirmed = true)
        {
            var meeting = new MeetingMinutes
            {
                MeetingTitle = "例会", MeetingContent = "确认任务", ProjectId = Project.Id, CreatorId = Leader.Id,
                ConfirmedAt = confirmed ? Now.AddDays(-1) : null, CreatedAt = Now.AddDays(-1)
            };
            Context.Add(meeting); await Context.SaveChangesAsync();
            var action = new MeetingActionItem
            {
                MeetingMinutesId = meeting.Id, Title = task.Title, MatchedTaskId = task.Id,
                AssigneeId = Owner.Id, Deadline = task.EndTime, IsConfirmed = confirmed,
                SyncStatus = confirmed ? "已创建" : "待确认创建", CreatedAt = Now.AddDays(-1)
            };
            Context.Add(action); await Context.SaveChangesAsync(); return action;
        }
        public async ValueTask DisposeAsync() { await Context.DisposeAsync(); await connection.DisposeAsync(); }
    }
}

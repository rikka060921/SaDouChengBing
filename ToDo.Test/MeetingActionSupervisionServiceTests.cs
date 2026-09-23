using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Test;

public sealed class MeetingActionSupervisionServiceTests
{
    [Fact]
    public async Task DueSoon_CreatesOneDailyAlertAndIsIdempotent()
    {
        await using var db = await SupervisionDatabase.CreateAsync();
        var now = new DateTime(2026, 8, 31, 9, 0, 0);
        var (_, action) = await db.AddConfirmedActionAsync(now.AddHours(6));
        var service = new MeetingActionSupervisionService(db.Context);

        var first = await service.RunAsync(now);
        var second = await service.RunAsync(now.AddHours(1));

        Assert.Equal(MeetingActionSupervisionStatus.DueSoon, action.SupervisionStatus);
        Assert.Equal(1, action.EscalationLevel);
        Assert.Equal(1, action.ReminderCount);
        Assert.Equal(1, first.EventsCreated);
        Assert.Equal(1, first.NotificationsCreated);
        Assert.Equal(0, second.EventsCreated);
        Assert.Equal(0, second.NotificationsCreated);
        Assert.Single(await db.Context.MeetingActionSupervisionEvents.AsNoTracking().ToListAsync());
        Assert.Equal(1, await db.Context.UserNotifications.CountAsync());
    }

    [Fact]
    public async Task Overdue_UpgradesFromOwnerReminderToProjectLeadership()
    {
        await using var db = await SupervisionDatabase.CreateAsync();
        var now = new DateTime(2026, 8, 31, 9, 0, 0);
        var (_, action) = await db.AddConfirmedActionAsync(now.AddHours(-2));
        var service = new MeetingActionSupervisionService(db.Context);

        var initial = await service.RunAsync(now);
        var escalated = await service.RunAsync(now.AddHours(26));

        Assert.Equal(1, initial.NotificationsCreated);
        Assert.Equal(2, escalated.NotificationsCreated);
        Assert.Equal(MeetingActionSupervisionStatus.Overdue, action.SupervisionStatus);
        Assert.Equal(2, action.EscalationLevel);
        Assert.Equal(2, action.ReminderCount);
        var events = await db.Context.MeetingActionSupervisionEvents.AsNoTracking()
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal(MeetingActionSupervisionEventType.StateChanged, events[0].EventType);
        Assert.Equal(MeetingActionSupervisionEventType.Escalation, events[1].EventType);
        Assert.Equal(3, await db.Context.UserNotifications.CountAsync());
    }

    [Fact]
    public async Task CompletedTransition_IsAuditedWithoutSendingAttentionAlert()
    {
        await using var db = await SupervisionDatabase.CreateAsync();
        var now = new DateTime(2026, 8, 31, 9, 0, 0);
        var (task, action) = await db.AddConfirmedActionAsync(now.AddDays(5));
        var service = new MeetingActionSupervisionService(db.Context);

        var onTrack = await service.RunAsync(now);
        task.SetStatus(ToDo.Entities.TaskStatus.Completed);
        task.ConcurrencyVersion++;
        await db.Context.SaveChangesAsync();
        var completed = await service.RunAsync(now.AddHours(1));

        Assert.Equal(MeetingActionSupervisionStatus.Completed, action.SupervisionStatus);
        Assert.Equal(1, onTrack.EventsCreated);
        Assert.Equal(1, completed.EventsCreated);
        Assert.Equal(0, onTrack.NotificationsCreated + completed.NotificationsCreated);
        Assert.Equal(2, await db.Context.MeetingActionSupervisionEvents.CountAsync());
        Assert.Empty(await db.Context.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task SelectedTaskContext_IncludesServerReceiptAndMeetingCommitmentEvidence()
    {
        await using var db = await SupervisionDatabase.CreateAsync();
        var now = new DateTime(2026, 8, 31, 9, 0, 0);
        var (task, action) = await db.AddConfirmedActionAsync(now.AddHours(4));
        await new MeetingActionSupervisionService(db.Context).RunAsync(now);
        var agent = new AgentDefinition
        {
            AgentKey = "context-evidence-test",
            Name = "上下文证据测试",
            SystemPrompt = "只读审查",
            RequiresProject = true,
            RequiresTask = true,
            IsEnabled = true,
            ContextSourcesJson = "[\"SelectedTask\"]"
        };
        var session = new AiSession
        {
            AgentKey = agent.AgentKey,
            UserId = db.Creator.Id,
            ProjectId = db.Project.Id,
            TaskId = task.Id,
            Prompt = "审查交付"
        };
        db.Context.AddRange(agent, session);
        await db.Context.SaveChangesAsync();
        var workItem = new AgentWorkItem
        {
            AgentDefinitionId = agent.Id,
            ProjectId = db.Project.Id,
            TaskId = task.Id,
            RequestedByUserId = db.Creator.Id,
            AiSessionId = session.Id,
            IdempotencyKey = "context-evidence-work",
            Status = AgentWorkItemStatus.Completed
        };
        db.Context.AgentWorkItems.Add(workItem);
        await db.Context.SaveChangesAsync();
        db.Context.AgentDeliveryReceipts.Add(new AgentDeliveryReceipt
        {
            AgentWorkItemId = workItem.Id,
            AgentDefinitionId = agent.Id,
            ProjectId = db.Project.Id,
            TaskId = task.Id,
            AiSessionId = session.Id,
            AgentVersion = 1,
            OutcomeSummary = "已提交可验收成果",
            EvidenceJson = "[{\"type\":\"task\",\"label\":\"状态已更新\"}]",
            ToolEffectsJson = "[]",
            ValidationSummary = "系统证据完整",
            RiskSummary = "仍需人工确认",
            ContentHash = new string('a', 64)
        });
        await db.Context.SaveChangesAsync();

        var context = await new AgentContextService(db.Context, null!)
            .BuildAsync(agent, session, db.Creator.Id);

        Assert.Contains("【最新交付凭证（系统事实）】", context);
        Assert.Contains("已提交可验收成果", context);
        Assert.Contains("【会议承诺督办（系统事实）】", context);
        Assert.Contains($"行动项 #{action.Id}", context);
        Assert.Contains("DueSoon L1", context);
    }

    [Fact]
    public async Task DistributedLease_AllowsOnlyOneSupervisorAndCanBeReleased()
    {
        await using var db = await SupervisionDatabase.CreateAsync();
        var firstService = new DistributedLeaseService(db.Context);
        await using var first = await firstService.TryAcquireAsync(
            "meeting-action-supervision:test",
            TimeSpan.FromMinutes(1));
        Assert.NotNull(first);

        var blocked = await new DistributedLeaseService(db.Context).TryAcquireAsync(
            "meeting-action-supervision:test",
            TimeSpan.FromMinutes(1));
        Assert.Null(blocked);

        await first!.DisposeAsync();
        await using var afterRelease = await new DistributedLeaseService(db.Context).TryAcquireAsync(
            "meeting-action-supervision:test",
            TimeSpan.FromMinutes(1));
        Assert.NotNull(afterRelease);
    }

    [Fact]
    public async Task AcknowledgedAction_DoesNotRepeatReminderBeforeCheckpoint()
    {
        await using var db = await SupervisionDatabase.CreateAsync();
        var now = new DateTime(2026, 8, 31, 9, 0, 0);
        var (_, action) = await db.AddConfirmedActionAsync(now.AddHours(-2));
        var service = new MeetingActionSupervisionService(db.Context);
        await service.RunAsync(now);
        action.SupervisionAcknowledgedByUserId = db.Owner.Id;
        action.SupervisionAcknowledgedAt = now.AddMinutes(5);
        action.NextCheckpointAt = now.AddDays(2);
        action.SnoozedUntil = now.AddDays(2);
        await db.Context.SaveChangesAsync();

        var nextDay = await service.RunAsync(now.AddHours(20));

        Assert.Equal(0, nextDay.EventsCreated);
        Assert.Equal(0, nextDay.NotificationsCreated);
        Assert.Equal(1, action.ReminderCount);
    }

    private sealed class SupervisionDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private SupervisionDatabase(
            SqliteConnection connection,
            ApplicationDbContext context,
            ApplicationUser leader,
            ApplicationUser creator,
            ApplicationUser owner,
            ApplicationUser admin,
            Project project)
        {
            _connection = connection;
            Context = context;
            Leader = leader;
            Creator = creator;
            Owner = owner;
            Admin = admin;
            Project = project;
        }

        public ApplicationDbContext Context { get; }
        public ApplicationUser Leader { get; }
        public ApplicationUser Creator { get; }
        public ApplicationUser Owner { get; }
        public ApplicationUser Admin { get; }
        public Project Project { get; }

        public static async Task<SupervisionDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();

            var leader = User("supervision-leader", "项目负责人");
            var creator = User("supervision-creator", "会议创建人");
            var owner = User("supervision-owner", "任务负责人");
            var admin = User("supervision-admin", "项目管理员");
            context.Users.AddRange(leader, creator, owner, admin);
            await context.SaveChangesAsync();

            var project = new Project
            {
                Name = "会议督办测试项目",
                CreatedByUserId = leader.Id,
                LeaderUserId = leader.Id,
                Requirements = "按会议承诺推进任务"
            };
            context.Project.Add(project);
            await context.SaveChangesAsync();
            context.ProjectUsers.AddRange(
                new ProjectUser { ProjectId = project.Id, UserId = creator.Id, ProjectRole = (int)ProjectRole.Member },
                new ProjectUser { ProjectId = project.Id, UserId = owner.Id, ProjectRole = (int)ProjectRole.Member },
                new ProjectUser { ProjectId = project.Id, UserId = admin.Id, ProjectRole = (int)ProjectRole.Admin });
            await context.SaveChangesAsync();
            return new SupervisionDatabase(connection, context, leader, creator, owner, admin, project);
        }

        public async Task<(ToDoTask Task, MeetingActionItem Action)> AddConfirmedActionAsync(DateTime deadline)
        {
            var meeting = new MeetingMinutes
            {
                MeetingTitle = "周例会",
                MeetingContent = "确认交付计划",
                MeetingDate = deadline.Date,
                CreatorId = Creator.Id,
                ProjectId = Project.Id,
                IsDraft = false,
                ConfirmedAt = deadline.AddDays(-2)
            };
            var task = new ToDoTask
            {
                Title = "提交可验收成果",
                Description = "附带证据完成交付",
                CreatorId = Creator.Id,
                AssigneeId = Owner.Id,
                ReviewerId = Admin.Id,
                ProjectId = Project.Id,
                Status = ToDo.Entities.TaskStatus.InProgress,
                EndTime = deadline
            };
            Context.AddRange(meeting, task);
            await Context.SaveChangesAsync();
            var action = new MeetingActionItem
            {
                MeetingMinutesId = meeting.Id,
                Title = "提交可验收成果",
                Content = "提交可验收成果",
                AssigneeId = Owner.Id,
                Deadline = deadline,
                MatchedTaskId = task.Id,
                IsConfirmed = true,
                SyncStatus = "已创建"
            };
            Context.MeetingActionItems.Add(action);
            await Context.SaveChangesAsync();
            return (task, action);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static ApplicationUser User(string userName, string realName) => new()
        {
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            RealName = realName,
            Status = UserStatus.Active,
            Role = UserRole.teamMember
        };
    }
}

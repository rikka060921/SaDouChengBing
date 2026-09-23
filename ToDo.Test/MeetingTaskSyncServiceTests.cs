using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;

namespace ToDo.Test;

public sealed class MeetingTaskSyncServiceTests
{
    [Fact]
    public async Task Confirm_UnauthorizedUser_DoesNotSubmitMeetingOrLockAgenda()
    {
        await using var db = await MeetingDatabase.CreateAsync();
        var meeting = await db.AddMeetingAsync(isDraft: true);
        var agenda = new MeetingAgenda
        {
            ProjectId = db.Project.Id,
            Title = "待讨论议题",
            Status = AgendaStatus.Active
        };
        db.Context.MeetingAgendas.Add(agenda);
        db.Context.MeetingActionItems.Add(new MeetingActionItem
        {
            MeetingMinutesId = meeting.Id,
            Title = "待确认建议",
            Content = "待确认建议",
            SyncStatus = "待确认创建"
        });
        await db.Context.SaveChangesAsync();

        var result = await db.Service.ConfirmAsync(meeting.Id, db.Member.Id);

        Assert.False(result.Success);
        db.Context.ChangeTracker.Clear();
        var savedMeeting = await db.Context.MeetingMinutes.SingleAsync(item => item.Id == meeting.Id);
        var savedAgenda = await db.Context.MeetingAgendas.SingleAsync(item => item.Id == agenda.Id);
        Assert.True(savedMeeting.IsDraft);
        Assert.Null(savedMeeting.SubmittedAt);
        Assert.Equal(AgendaStatus.Active, savedAgenda.Status);
        Assert.Null(savedAgenda.ArchivedByMeetingId);
    }

    [Fact]
    public async Task Confirm_TaskChange_UsesReviewedAfterFieldsAndKeepsLegacyDefaultsFromOverwritingTask()
    {
        await using var db = await MeetingDatabase.CreateAsync();
        var meeting = await db.AddMeetingAsync(isDraft: false);
        var deadline = AppTime.Today.AddDays(7);
        var task = new ToDoTask
        {
            Title = "既有任务",
            CreatorId = db.Admin.Id,
            AssigneeId = db.Admin.Id,
            ProjectId = db.Project.Id,
            Status = ToDo.Entities.TaskStatus.InProgress,
            Priority = TaskPriority.High,
            EndTime = deadline,
            Progress = 60
        };
        db.Context.ToDoTasks.Add(task);
        await db.Context.SaveChangesAsync();
        db.Context.MeetingActionItems.Add(new MeetingActionItem
        {
            MeetingMinutesId = meeting.Id,
            Title = task.Title,
            Content = task.Title,
            ActionType = "TaskChange",
            MatchedTaskId = task.Id,
            SyncStatus = "待确认更新",
            // These are legacy/display defaults and must not drive the update.
            TaskStatus = "NotStarted",
            Priority = "Medium",
            BeforeStatus = "InProgress",
            AfterStatus = "Completed",
            BeforeAssigneeId = db.Admin.Id,
            AfterAssigneeId = db.Admin.Id,
            BeforeDeadline = deadline,
            AfterDeadline = deadline,
            BeforePriority = "High",
            AfterPriority = "High"
        });
        await db.Context.SaveChangesAsync();

        var result = await db.Service.ConfirmAsync(meeting.Id, db.Admin.Id);

        Assert.True(result.Success);
        db.Context.ChangeTracker.Clear();
        var savedTask = await db.Context.ToDoTasks.SingleAsync(item => item.Id == task.Id);
        var savedItem = await db.Context.MeetingActionItems.SingleAsync();
        Assert.Equal(ToDo.Entities.TaskStatus.Completed, savedTask.Status);
        Assert.True(savedTask.IsCompleted);
        Assert.Equal(100, savedTask.Progress);
        Assert.Equal(TaskPriority.High, savedTask.Priority);
        Assert.Equal(db.Admin.Id, savedTask.AssigneeId);
        Assert.Equal(deadline, savedTask.EndTime);
        Assert.True(savedItem.IsConfirmed);
    }

    [Fact]
    public async Task Confirm_RepeatedRequest_DoesNotCreateDuplicateTaskOrNotification()
    {
        await using var db = await MeetingDatabase.CreateAsync();
        var meeting = await db.AddMeetingAsync(isDraft: true);
        db.Context.MeetingActionItems.Add(new MeetingActionItem
        {
            MeetingMinutesId = meeting.Id,
            Title = "发布验收版本",
            Content = "发布验收版本",
            SyncStatus = "待确认创建"
        });
        await db.Context.SaveChangesAsync();

        var first = await db.Service.ConfirmAsync(meeting.Id, db.Admin.Id);
        var second = await db.Service.ConfirmAsync(meeting.Id, db.Admin.Id);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(1, await db.Context.ToDoTasks.CountAsync());
        Assert.Equal(1, await db.Context.UserNotifications.CountAsync());
    }

    [Fact]
    public async Task Confirm_LateFailure_RollsBackMeetingTaskAndActionItem()
    {
        await using var db = await MeetingDatabase.CreateAsync();
        var meeting = await db.AddMeetingAsync(isDraft: true);
        db.Context.MeetingActionItems.Add(new MeetingActionItem
        {
            MeetingMinutesId = meeting.Id,
            Title = "需要整体回滚的任务",
            Content = "需要整体回滚的任务",
            SyncStatus = "待确认创建"
        });
        await db.Context.SaveChangesAsync();
        var service = db.CreateService(new ThrowingTaskDomainService(db.Context));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(meeting.Id, db.Admin.Id));

        db.Context.ChangeTracker.Clear();
        var savedMeeting = await db.Context.MeetingMinutes.SingleAsync(item => item.Id == meeting.Id);
        var savedItem = await db.Context.MeetingActionItems.SingleAsync();
        Assert.True(savedMeeting.IsDraft);
        Assert.Null(savedMeeting.ConfirmedAt);
        Assert.Empty(await db.Context.ToDoTasks.ToListAsync());
        Assert.StartsWith("待确认", savedItem.SyncStatus);
        Assert.False(savedItem.IsConfirmed);
    }

    [Fact]
    public async Task Confirm_TaskChangeFromAnotherProject_IsRejectedWithoutSideEffects()
    {
        await using var db = await MeetingDatabase.CreateAsync();
        var otherProject = new Project
        {
            Name = "其他项目",
            CreatedByUserId = db.Admin.Id,
            LeaderUserId = db.Admin.Id
        };
        db.Context.Project.Add(otherProject);
        await db.Context.SaveChangesAsync();
        var otherTask = new ToDoTask
        {
            Title = "其他项目任务",
            CreatorId = db.Admin.Id,
            ProjectId = otherProject.Id,
            Status = ToDo.Entities.TaskStatus.NotStarted
        };
        db.Context.ToDoTasks.Add(otherTask);
        var meeting = await db.AddMeetingAsync(isDraft: true);
        db.Context.MeetingActionItems.Add(new MeetingActionItem
        {
            MeetingMinutesId = meeting.Id,
            Title = "越权变更",
            Content = "越权变更",
            ActionType = "TaskChange",
            MatchedTaskId = otherTask.Id,
            SyncStatus = "待确认更新",
            BeforeStatus = "NotStarted",
            AfterStatus = "Completed"
        });
        await db.Context.SaveChangesAsync();

        var result = await db.Service.ConfirmAsync(meeting.Id, db.Admin.Id);

        Assert.False(result.Success);
        db.Context.ChangeTracker.Clear();
        Assert.Equal(ToDo.Entities.TaskStatus.NotStarted,
            (await db.Context.ToDoTasks.SingleAsync(item => item.Id == otherTask.Id)).Status);
        Assert.True((await db.Context.MeetingMinutes.SingleAsync(item => item.Id == meeting.Id)).IsDraft);
    }

    [Fact]
    public async Task Prepare_SuccessfulParseWithNoTasks_DoesNotInvokeLegacyFallback()
    {
        await using var db = await MeetingDatabase.CreateAsync();
        var meeting = await db.AddMeetingAsync(isDraft: false);
        db.AI.FullResult = new AIMeetingFullParseResult
        {
            Success = true,
            KeyPoints = "会议只有决策，没有行动项",
            MeetingPurpose = "确认方案"
        };

        var result = await db.Service.PrepareAsync(meeting.Id, db.Admin.Id);

        Assert.True(result.Success);
        Assert.Equal(0, db.AI.LegacyMeetingCallCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Prepare_DecisionQuotes_PersistsOnlySourceEvidenceAndFallsBackToConclusion()
    {
        await using var db = await MeetingDatabase.CreateAsync();
        var meeting = await db.AddMeetingAsync(isDraft: false);
        meeting.TranscriptText = """
            赵冬：这个功能接下来怎么办？
            乔宽：我建议先灰度验证。
            赵冬：好，那就先灰度，下周验收。
            """;
        await db.Context.SaveChangesAsync();
        db.AI.FullResult = new AIMeetingFullParseResult
        {
            Success = true,
            KeyPoints = "会议确认灰度方案",
            MeetingPurpose = "确认上线策略",
            Decisions =
            [
                new MeetingDecisionItem
                {
                    Content = "先灰度并在下周验收",
                    DecisionMaker = "赵冬",
                    KeyQuote = "决定明天直接正式上线",
                    OriginalQuote = "旧字段中的伪原话",
                    OriginalQuotes =
                    [
                        "赵冬:这个功能接下来怎么办",
                        "完全不存在于原文的讨论",
                        "赵冬：好，那就先灰度，下周验收。"
                    ]
                }
            ]
        };

        var result = await db.Service.PrepareAsync(meeting.Id, db.Admin.Id);

        Assert.True(result.Success);
        db.Context.ChangeTracker.Clear();
        var saved = await db.Context.MeetingMinutes.SingleAsync(item => item.Id == meeting.Id);
        var decisions = System.Text.Json.JsonSerializer.Deserialize<List<MeetingDecisionItem>>(
            saved.AiDecisionsJson!,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var decision = Assert.Single(decisions!);
        Assert.Equal(
            ["赵冬：这个功能接下来怎么办", "赵冬：好，那就先灰度，下周验收。"],
            decision.OriginalQuotes);
        Assert.Equal("赵冬：好，那就先灰度，下周验收。", decision.KeyQuote);
        Assert.Equal(decision.KeyQuote, decision.OriginalQuote);
        Assert.DoesNotContain("决定明天直接正式上线", saved.AiDecisionsJson);
        Assert.DoesNotContain("完全不存在于原文的讨论", saved.AiDecisionsJson);
        Assert.DoesNotContain("旧字段中的伪原话", saved.AiDecisionsJson);
    }

    [Fact]
    public async Task Prepare_TaskFromDecision_LinksDecisionSnapshotAndIgnoresInvalidIndex()
    {
        await using var db = await MeetingDatabase.CreateAsync();
        var meeting = await db.AddMeetingAsync(isDraft: false);
        db.AI.FullResult = new AIMeetingFullParseResult
        {
            Success = true,
            KeyPoints = "会议形成决策并派生出任务",
            MeetingPurpose = "确定改造方向",
            Decisions =
            [
                new MeetingDecisionItem { Content = "会议准备页改为三个列表展示", DecisionMaker = "赵冬" },
                new MeetingDecisionItem { Content = "智能体模块暂不设置紧急截止时间", DecisionMaker = "赵冬" }
            ],
            TaskItems =
            [
                new MeetingTaskParseItem
                {
                    Title = "改造会议准备页任务展示",
                    Description = "拆成三个互不重复的任务列表",
                    AssigneeName = "会议管理员",
                    SourceDecisionIndex = 1
                },
                new MeetingTaskParseItem
                {
                    Title = "越界序号任务",
                    Description = "序号超出决策范围，应被忽略不关联",
                    SourceDecisionIndex = 9
                }
            ]
        };

        var result = await db.Service.PrepareAsync(meeting.Id, db.Admin.Id);

        Assert.True(result.Success);
        db.Context.ChangeTracker.Clear();
        var items = await db.Context.MeetingActionItems
            .Where(item => item.MeetingMinutesId == meeting.Id)
            .ToListAsync();
        var linked = items.Single(item => item.Title == "改造会议准备页任务展示");
        Assert.Equal(1, linked.SourceDecisionIndex);
        Assert.Equal("会议准备页改为三个列表展示", linked.SourceDecisionContent);

        var unlinked = items.Single(item => item.Title == "越界序号任务");
        Assert.Null(unlinked.SourceDecisionIndex);
        Assert.Null(unlinked.SourceDecisionContent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("[{\"taskId\":1,\"discussed\":true}]")]
    [InlineData("[{\"taskId\":1,\"discussed\":true,\"rejected\":true,\"changed\":false}]")]
    public async Task Confirm_UncertainOrRejectedDiscussion_DoesNotApprovePendingTask(string? response)
    {
        await using var db = await MeetingDatabase.CreateAsync();
        var meeting = await db.AddMeetingAsync(isDraft: false);
        var task = new ToDoTask { Title = "支付接口验收", ProjectId = db.Project.Id, CreatorId = db.Admin.Id, Status = ToDo.Entities.TaskStatus.PendingConfirmation };
        db.Context.ToDoTasks.Add(task);
        await db.Context.SaveChangesAsync();
        var agenda = new MeetingAgenda { ProjectId = db.Project.Id, SourceId = task.Id, Title = task.Title, Status = AgendaStatus.Active };
        db.Context.MeetingAgendas.Add(agenda);
        meeting.TranscriptText = "支付接口验收不通过，打回重做，不能标记已完成。";
        db.AI.ChatResponse = response;
        await db.Context.SaveChangesAsync();
        var result = await db.Service.ConfirmAsync(meeting.Id, db.Admin.Id);
        Assert.True(result.Success);
        Assert.Equal(ToDo.Entities.TaskStatus.PendingConfirmation, task.Status);
        Assert.Equal(AgendaStatus.Active, agenda.Status);
        Assert.Empty(await db.Context.ChangeLogs.ToListAsync());
    }

    [Fact]
    public async Task Confirm_ClearDiscussionApproval_ArchivesAndRecordsReview()
    {
        await using var db = await MeetingDatabase.CreateAsync();
        var meeting = await db.AddMeetingAsync(isDraft: false);
        var task = new ToDoTask { Title = "支付接口验收", ProjectId = db.Project.Id, CreatorId = db.Admin.Id, Status = ToDo.Entities.TaskStatus.PendingConfirmation };
        db.Context.ToDoTasks.Add(task);
        await db.Context.SaveChangesAsync();
        var agenda = new MeetingAgenda { ProjectId = db.Project.Id, SourceId = task.Id, Title = task.Title, Status = AgendaStatus.Active };
        db.Context.MeetingAgendas.Add(agenda);
        meeting.TranscriptText = "支付接口验收通过，没有修改。";
        db.AI.ChatResponse = $"[{{\"taskId\":{task.Id},\"discussed\":true,\"rejected\":false,\"changed\":false}}]";
        await db.Context.SaveChangesAsync();
        var result = await db.Service.ConfirmAsync(meeting.Id, db.Admin.Id);
        Assert.True(result.Success);
        Assert.Equal(ToDo.Entities.TaskStatus.Completed, task.Status);
        Assert.Equal(AgendaStatus.Archived, agenda.Status);
        Assert.Single(await db.Context.ChangeLogs.Where(l => l.TaskId == task.Id).ToListAsync());
    }

    private sealed class MeetingDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private MeetingDatabase(
            SqliteConnection connection,
            ApplicationDbContext context,
            ApplicationUser admin,
            ApplicationUser member,
            Project project)
        {
            _connection = connection;
            Context = context;
            Admin = admin;
            Member = member;
            Project = project;
            AI = new StubAIService();
            Service = CreateService(new NoopTaskDomainService(context));
        }

        public ApplicationDbContext Context { get; }
        public ApplicationUser Admin { get; }
        public ApplicationUser Member { get; }
        public Project Project { get; }
        public StubAIService AI { get; }
        public MeetingTaskSyncService Service { get; }

        public MeetingTaskSyncService CreateService(ToDoTaskDomainService taskDomainService) => new(
            Context,
            AI,
            new UserNotificationService(Context),
            taskDomainService,
            new AgentWorkQueueService(Context, null!, null!, new UserNotificationService(Context),
                null!, new AgentOutcomeService(Context), NullLogger<AgentWorkQueueService>.Instance));

        public static async Task<MeetingDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();

            var admin = new ApplicationUser
            {
                UserName = "meeting-admin",
                NormalizedUserName = "MEETING-ADMIN",
                RealName = "会议管理员",
                Role = UserRole.systemAdmin,
                Status = UserStatus.Active
            };
            var member = new ApplicationUser
            {
                UserName = "meeting-member",
                NormalizedUserName = "MEETING-MEMBER",
                RealName = "普通成员",
                Role = UserRole.teamMember,
                Status = UserStatus.Active
            };
            context.Users.AddRange(admin, member);
            await context.SaveChangesAsync();

            var project = new Project
            {
                Name = "会议同步测试项目",
                CreatedByUserId = admin.Id,
                LeaderUserId = admin.Id
            };
            context.Project.Add(project);
            await context.SaveChangesAsync();
            context.ProjectUsers.Add(new ProjectUser
            {
                ProjectId = project.Id,
                UserId = member.Id,
                ProjectRole = (int)ProjectRole.Member
            });
            await context.SaveChangesAsync();
            return new MeetingDatabase(connection, context, admin, member, project);
        }

        public async Task<MeetingMinutes> AddMeetingAsync(bool isDraft)
        {
            var meeting = new MeetingMinutes
            {
                MeetingTitle = "同步测试会议",
                MeetingContent = "会议内容",
                MeetingDate = AppTime.Today,
                CreatorId = Admin.Id,
                ProjectId = Project.Id,
                IsDraft = isDraft
            };
            Context.MeetingMinutes.Add(meeting);
            await Context.SaveChangesAsync();
            return meeting;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class NoopTaskDomainService : ToDoTaskDomainService
    {
        public NoopTaskDomainService(ApplicationDbContext context)
            : base(context, null!, NullLogger<ProjectDomain>.Instance)
        {
        }

        public override Task LogTaskOperationAsync(
            OperationType operationType,
            OperationTarget target,
            int operatorUserId,
            string? beforeState = null,
            string? afterState = null,
            OperationStatus status = OperationStatus.成功,
            int? projectId = null,
            string? projectName = null,
            int? taskId = null,
            string? taskTitle = null,
            string? taskName = null,
            int? targetId = null,
            string? targetName = null) => Task.CompletedTask;
    }

    private sealed class ThrowingTaskDomainService : ToDoTaskDomainService
    {
        public ThrowingTaskDomainService(ApplicationDbContext context)
            : base(context, null!, NullLogger<ProjectDomain>.Instance)
        {
        }

        public override Task LogTaskOperationAsync(
            OperationType operationType,
            OperationTarget target,
            int operatorUserId,
            string? beforeState = null,
            string? afterState = null,
            OperationStatus status = OperationStatus.成功,
            int? projectId = null,
            string? projectName = null,
            int? taskId = null,
            string? taskTitle = null,
            string? taskName = null,
            int? targetId = null,
            string? targetName = null) => throw new InvalidOperationException("模拟审计日志写入失败");
    }

    private sealed class StubAIService : IAIService
    {
        public AIMeetingFullParseResult FullResult { get; set; } = new() { Success = true };
        public int LegacyMeetingCallCount { get; private set; }
        public string? ChatResponse { get; set; }

        public Task<AITaskSplitResult> SplitTaskAsync(string taskTitle, string taskDescription, string? expectedSubTaskCount = null) => throw new NotSupportedException();
        public Task<AIMeetingSummaryResult> ProcessMeetingMinutesAsync(string meetingContent)
        {
            LegacyMeetingCallCount++;
            return Task.FromResult(new AIMeetingSummaryResult { Success = true });
        }
        public Task<AIMeetingFullParseResult> ProcessMeetingMinutesFullStructAsync(string meetingContent, List<string>? memberNames = null, List<string>? projectNames = null) => Task.FromResult(FullResult);
        public Task<string> GetChatCompletionAsync(string prompt) => throw new NotSupportedException();
        public Task<string> GetChatCompletionAsync(string prompt, AIChatOptions options, CancellationToken cancellationToken = default)
            => ChatResponse == null ? throw new InvalidOperationException("模拟 AI 不可用") : Task.FromResult(ChatResponse);
        public Task<AICompletionResult> GetChatCompletionWithUsageAsync(string prompt, AIChatOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AIDailyReportResult> GenerateDailyReportAsync(AIDailyReportInput input) => throw new NotSupportedException();
        public Task<AIDailyReportResult> GenerateDailyReportByProjectAsync(DailyReportByProjectInput input) => throw new NotSupportedException();
        public Task<AITaskParseResult> ParseTaskTextAsync(string text, string? expectedCount = null) => throw new NotSupportedException();
        public Task<AIDailyReportResult> GenerateTeamReportAsync(TeamReportInput input) => throw new NotSupportedException();
        public Task<AIMeetingMinutesResult> GenerateMeetingMinutesAsync(string transcript, string template, int projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

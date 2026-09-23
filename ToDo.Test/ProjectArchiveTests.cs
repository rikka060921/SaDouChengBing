using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;
using TaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Test;

public sealed partial class AgentFrameworkCompletionTests
{
    [Theory]
    [InlineData(AgentRunJobStatus.Pending)]
    [InlineData(AgentRunJobStatus.Running)]
    [InlineData(AgentRunJobStatus.WaitingApproval)]
    [InlineData(AgentRunJobStatus.Retrying)]
    public async Task Archive_ManualQueueStatesBlock(AgentRunJobStatus status)
    {
        await using var db = await TestDatabase.CreateAsync();
        var job = await db.Queue.EnqueueNewAsync(db.Agent.AgentKey, "分析", db.Admin, db.Project.Id, null);
        job.Status = status;
        await db.Context.SaveChangesAsync();
        Assert.False((await ProjectLifecycleRules.CheckArchiveAsync(db.Context, db.Project.Id)).CanArchive);
    }

    [Theory]
    [InlineData("newMeeting")]
    [InlineData("newTask")]
    [InlineData("prepTaskIds")]
    [InlineData("category")]
    [InlineData("schedule")]
    [InlineData("subscription")]
    [InlineData("report")]
    [InlineData("label")]
    public async Task Archive_NewBusinessRejectedWithoutChangingHistoricalData(string kind)
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = new ToDoTask { ProjectId = db.Project.Id, CreatorId = db.Admin.Id, Title = "历史任务" };
        db.Context.Add(task);
        await db.Context.SaveChangesAsync();
        await ProjectDomainFor(db).SetArchiveStatusAsync(db.Project.Id, true, db.Admin.Id, UserRole.systemAdmin, true);
        switch (kind)
        {
            case "newMeeting": db.Context.Add(new MeetingMinutes { ProjectId = db.Project.Id, CreatorId = db.Admin.Id, MeetingTitle = "新会议" }); break;
            case "newTask": db.Context.Add(new ToDoTask { ProjectId = db.Project.Id, CreatorId = db.Admin.Id, Title = "新任务" }); break;
            case "prepTaskIds": db.Context.Add(new MeetingPrepDraft { CreatorId = db.Admin.Id, Title = "绕过项目选择", SelectedTaskIdsJson = $"[{task.Id}]" }); break;
            case "report": db.Context.Add(new DailyReport { ProjectId = db.Project.Id, ReporterId = db.Admin.Id, ReportTitle = "新报告" }); break;
            case "label": db.Context.Add(new TaskLabel { ProjectId = db.Project.Id, Name = "新标签" }); break;
            case "category": db.Context.Add(new DocumentCategory { ProjectId = db.Project.Id, Name = "分类" }); break;
            case "schedule": db.Context.Add(new ScheduledJob { ProjectId = db.Project.Id, CreatedById = db.Admin.Id, JobKey = "archived" }); break;
            case "subscription": db.Context.Add(new AgentEventSubscription { ProjectId = db.Project.Id, AgentDefinitionId = db.Agent.Id, CreatedByUserId = db.Admin.Id, Name = "新规则", EventType = "task.created" }); break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Context.SaveChangesAsync());
        Assert.Single(await db.Context.ToDoTasks.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Archive_LegacyQueuedJobsCannotClaim_AndRestoreDoesNotRestartThem()
    {
        await using var db = await TestDatabase.CreateAsync();
        var job = await db.Queue.EnqueueNewAsync(db.Agent.AgentKey, "旧工作", db.Admin, db.Project.Id, null);
        // Simulate historical data imported before archive guards existed.
        await db.Context.Project.Where(p => p.Id == db.Project.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ProjectStatus.Archived));
        await db.Context.Entry(db.Project).ReloadAsync();
        Assert.Null(await db.Queue.ClaimNextAsync());
        Assert.True(await ProjectDomainFor(db).SetArchiveStatusAsync(db.Project.Id, false, db.Admin.Id, UserRole.systemAdmin));
        Assert.Equal(AgentRunJobStatus.Cancelled, (await db.Context.AgentRunJobs.AsNoTracking().SingleAsync()).Status);
        Assert.Null(await db.Queue.ClaimNextAsync());
    }

    [Fact]
    public async Task Archive_StaleTrackedProjectCannotWriteBusinessContent()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.Context.Project.Where(p => p.Id == db.Project.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ProjectStatus.Archived));
        db.Project.Description = "stale update";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Context.SaveChangesAsync());
        Assert.NotEqual("stale update", (await db.Context.Project.AsNoTracking().SingleAsync()).Description);
    }

    private static ProjectDomain ProjectDomainFor(TestDatabase db) => new(db.Context, NullLogger<ProjectDomain>.Instance);

    [Fact]
    public async Task Archive_ListDefaultsActive_AllStillHonorsVisibilityAndDeletion()
    {
        await using var db = await TestDatabase.CreateAsync();
        var archived = new Project { Name = "旧项目", CreatedByUserId = db.Admin.Id, LeaderUserId = db.Admin.Id, Status = ProjectStatus.Archived };
        var privateProject = new Project { Name = "私密", CreatedByUserId = db.Admin.Id, LeaderUserId = db.Admin.Id, IsEncrypted = '2' };
        var deleted = new Project { Name = "删除", CreatedByUserId = db.Admin.Id, LeaderUserId = db.Admin.Id, IsDeleted = true };
        db.Context.AddRange(archived, privateProject, deleted);
        await db.Context.SaveChangesAsync();
        var service = ProjectDomainFor(db);
        var active = await service.GetProjectsAsync(db.Admin.Id, UserRole.systemAdmin);
        Assert.Equal(2, active.TotalCount);
        Assert.All(active.Projects, p => Assert.Equal(ProjectStatus.Active, p.Status));
        var old = await service.GetProjectsAsync(db.Admin.Id, UserRole.systemAdmin, isArchivedFilter: true);
        Assert.Equal(archived.Id, Assert.Single(old.Projects).Id);
        var all = await service.GetProjectsAsync(db.Member.Id, UserRole.teamMember, isArchivedFilter: null);
        Assert.Equal(2, all.TotalCount);
        Assert.DoesNotContain(all.Projects, p => p.Id == privateProject.Id || p.Id == deleted.Id);
        var search = await service.GetProjectsAsync(db.Admin.Id, UserRole.systemAdmin, keyword: "旧项目", isArchivedFilter: null);
        Assert.Equal(archived.Id, Assert.Single(search.Projects).Id);
    }

    [Fact]
    public async Task Archive_RequiresConfirmation_PreservesTasksHistoryAndCompletion()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = new ToDoTask { Title = "继续保留", CreatorId = db.Admin.Id, ProjectId = db.Project.Id, Progress = 37, Status = TaskStatus.InProgress };
        db.Context.Add(task);
        await db.Context.SaveChangesAsync();
        var service = ProjectDomainFor(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetArchiveStatusAsync(db.Project.Id, true, db.Admin.Id, UserRole.systemAdmin));
        Assert.Equal(ProjectStatus.Active, (await db.Context.Project.AsNoTracking().SingleAsync()).Status);
        Assert.True(await service.SetArchiveStatusAsync(db.Project.Id, true, db.Admin.Id, UserRole.systemAdmin, true));
        var saved = await db.Context.ToDoTasks.AsNoTracking().SingleAsync();
        Assert.Equal(TaskStatus.InProgress, saved.Status);
        Assert.Equal(37, saved.Progress);
        Assert.False(saved.IsCompleted);
        Assert.False((await db.Context.Project.AsNoTracking().SingleAsync()).IsDeleted);
        Assert.True(await service.CanViewProjectAsync(db.Project.Id, db.Admin.Id, UserRole.systemAdmin));
        Assert.False(await service.SetArchiveStatusAsync(db.Project.Id, false, db.Member.Id, UserRole.teamMember));
        Assert.True(await service.SetArchiveStatusAsync(db.Project.Id, false, db.Admin.Id, UserRole.systemAdmin));
        Assert.NotNull(await db.Context.ProjectSummaryCheckpoints.SingleOrDefaultAsync());
        Assert.Single(await db.Context.ProjectActivityRecords.Where(a => a.FieldName == "AutomationResumeBoundary").ToListAsync());
        Assert.Equal(37, (await db.Context.ToDoTasks.AsNoTracking().SingleAsync()).Progress);
    }

    [Theory]
    [InlineData(AgentWorkItemStatus.Pending)]
    [InlineData(AgentWorkItemStatus.Running)]
    [InlineData(AgentWorkItemStatus.Retrying)]
    [InlineData(AgentWorkItemStatus.Paused)]
    [InlineData(AgentWorkItemStatus.WaitingApproval)]
    [InlineData(AgentWorkItemStatus.WaitingPlanConfirmation)]
    public async Task Archive_BlocksEveryUnfinishedWorkState(AgentWorkItemStatus status)
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = new ToDoTask { Title = "AI 工作", CreatorId = db.Admin.Id, ProjectId = db.Project.Id };
        db.Context.Add(task);
        await db.Context.SaveChangesAsync();
        db.Context.AgentWorkItems.Add(new AgentWorkItem { ProjectId = db.Project.Id, TaskId = task.Id, AgentDefinitionId = db.Agent.Id, RequestedByUserId = db.Admin.Id, Status = status, IdempotencyKey = "archive-test" });
        await db.Context.SaveChangesAsync();
        Assert.False((await ProjectLifecycleRules.CheckArchiveAsync(db.Context, db.Project.Id)).CanArchive);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProjectDomainFor(db).SetArchiveStatusAsync(db.Project.Id, true, db.Admin.Id, UserRole.systemAdmin, true));
    }

    [Theory]
    [InlineData(AgentWorkItemStatus.Completed)]
    [InlineData(AgentWorkItemStatus.Failed)]
    [InlineData(AgentWorkItemStatus.Cancelled)]
    public async Task Archive_TerminalWorkDoesNotBlock(AgentWorkItemStatus status)
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = new ToDoTask { Title = "历史 AI 工作", CreatorId = db.Admin.Id, ProjectId = db.Project.Id };
        db.Context.Add(task);
        await db.Context.SaveChangesAsync();
        db.Context.AgentWorkItems.Add(new AgentWorkItem { ProjectId = db.Project.Id, TaskId = task.Id, AgentDefinitionId = db.Agent.Id, RequestedByUserId = db.Admin.Id, Status = status, IdempotencyKey = "archive-terminal" });
        await db.Context.SaveChangesAsync();
        Assert.True((await ProjectLifecycleRules.CheckArchiveAsync(db.Context, db.Project.Id)).CanArchive);
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("acceptance")]
    [InlineData("manualRun")]
    [InlineData("scheduled")]
    public async Task Archive_BlocksOutstandingBusiness(string kind)
    {
        await using var db = await TestDatabase.CreateAsync();
        if (kind == "approval") db.Context.Add(new ApprovalRequest { ProjectId = db.Project.Id, RequestedById = db.Admin.Id, Status = ApprovalRequestStatus.Pending });
        if (kind == "acceptance") db.Context.Add(new ToDoTask { ProjectId = db.Project.Id, CreatorId = db.Admin.Id, Title = "待验收", Status = TaskStatus.PendingConfirmation });
        if (kind == "scheduled") db.Context.Add(new ScheduledJob { ProjectId = db.Project.Id, CreatedById = db.Admin.Id, JobKey = "test", IsRunning = true });
        if (kind == "manualRun") await db.Queue.EnqueueNewAsync(db.Agent.AgentKey, "分析", db.Admin, db.Project.Id, null);
        await db.Context.SaveChangesAsync();
        Assert.False((await ProjectLifecycleRules.CheckArchiveAsync(db.Context, db.Project.Id)).CanArchive);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("group")]
    [InlineData("comment")]
    [InlineData("meeting")]
    [InlineData("meetingLink")]
    [InlineData("meetingVersion")]
    [InlineData("attachment")]
    [InlineData("action")]
    [InlineData("document")]
    [InlineData("prep")]
    [InlineData("member")]
    [InlineData("project")]
    public async Task Archive_RejectsTrackedWritesIncludingIndirectRelations(string kind)
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = new ToDoTask { ProjectId = db.Project.Id, CreatorId = db.Admin.Id, Title = "历史任务" };
        var meeting = new MeetingMinutes { ProjectId = db.Project.Id, CreatorId = db.Admin.Id, MeetingTitle = "历史会议", MeetingContent = "原文" };
        db.Context.AddRange(task, meeting);
        await db.Context.SaveChangesAsync();
        await ProjectDomainFor(db).SetArchiveStatusAsync(db.Project.Id, true, db.Admin.Id, UserRole.systemAdmin, true);
        switch (kind)
        {
            case "task": task.Title = "篡改"; break;
            case "group": db.Context.Add(new TaskGroup { ProjectId = db.Project.Id, CreatorId = db.Admin.Id, Name = "新分组" }); break;
            case "comment": db.Context.Add(new TaskComment { TaskId = task.Id, AuthorId = db.Admin.Id, Content = "新评论" }); break;
            case "meeting": meeting.MeetingTitle = "篡改"; break;
            case "meetingLink": db.Context.Add(new MeetingMinutesProject { ProjectId = db.Project.Id, MeetingMinutesId = meeting.Id }); break;
            case "meetingVersion": db.Context.Add(new MeetingVersion { MeetingMinutesId = meeting.Id, EditorId = db.Admin.Id, MeetingTitle = "新版" }); break;
            case "attachment": db.Context.Add(new MeetingAttachment { MeetingMinutesId = meeting.Id, FileName = "新附件" }); break;
            case "action": db.Context.Add(new MeetingActionItem { MeetingMinutesId = meeting.Id, Description = "新行动" }); break;
            case "document": db.Context.Add(new ProjectDocument { ProjectId = db.Project.Id, UploadedById = db.Admin.Id, FileName = "设计.md" }); break;
            case "prep": db.Context.Add(new MeetingPrepDraft { CreatorId = db.Admin.Id, Title = "准备", SelectedProjectIdsJson = $"[{db.Project.Id}]" }); break;
            case "member": db.Context.Add(new ProjectUser { ProjectId = db.Project.Id, UserId = db.Member.Id }); break;
            case "project": db.Project.Name = "篡改"; break;
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Context.SaveChangesAsync());
        Assert.Equal(ProjectLifecycleRules.ReadOnlyMessage, error.Message);
    }

    [Fact]
    public async Task Archive_RejectsMovingTaskOutAndMultProjectMeetingMutation()
    {
        await using var db = await TestDatabase.CreateAsync();
        var other = new Project { Name = "另一个项目", CreatedByUserId = db.Admin.Id, LeaderUserId = db.Admin.Id };
        db.Context.Add(other);
        await db.Context.SaveChangesAsync();
        var task = new ToDoTask { ProjectId = db.Project.Id, CreatorId = db.Admin.Id, Title = "历史任务" };
        var meeting = new MeetingMinutes { ProjectId = other.Id, CreatorId = db.Admin.Id, MeetingTitle = "跨项目会议", MeetingContent = "原文" };
        meeting.MeetingProjects.Add(new MeetingMinutesProject { ProjectId = db.Project.Id });
        db.Context.AddRange(task, meeting);
        await db.Context.SaveChangesAsync();
        await ProjectDomainFor(db).SetArchiveStatusAsync(db.Project.Id, true, db.Admin.Id, UserRole.systemAdmin, true);
        task.ProjectId = other.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Context.SaveChangesAsync());
        await db.Context.Entry(task).ReloadAsync();
        meeting.MeetingContent = "不能修改跨项目历史";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task Archive_ReadOnlyHistoryAndSecurityRevocationRemainAvailable()
    {
        await using var db = await TestDatabase.CreateAsync();
        var member = new ProjectUser { ProjectId = db.Project.Id, UserId = db.Member.Id };
        db.Context.Add(member);
        await db.Context.SaveChangesAsync();
        await ProjectDomainFor(db).SetArchiveStatusAsync(db.Project.Id, true, db.Admin.Id, UserRole.systemAdmin);
        db.Context.Remove(member);
        db.Context.UserNotifications.Add(new UserNotification { UserId = db.Admin.Id, Title = "安全异常", Content = "仍需人工处理" });
        await db.Context.SaveChangesAsync();
        Assert.Single(await db.Context.UserNotifications.ToListAsync());
        Assert.Empty(await db.Context.ProjectUsers.ToListAsync());
        Assert.True(await ProjectDomainFor(db).CanViewProjectAsync(db.Project.Id, db.Admin.Id, UserRole.systemAdmin));
    }

    [Fact]
    public async Task Archive_PreventsFormalEnqueueAndOrdinaryOverdueReminders()
    {
        await using var db = await TestDatabase.CreateAsync();
        var task = new ToDoTask { ProjectId = db.Project.Id, CreatorId = db.Admin.Id, AssigneeId = db.Member.Id, Title = "逾期", EndTime = AppTime.Now.AddDays(-3) };
        db.Context.Add(task);
        await db.Context.SaveChangesAsync();
        await ProjectDomainFor(db).SetArchiveStatusAsync(db.Project.Id, true, db.Admin.Id, UserRole.systemAdmin, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Queue.EnqueueNewAsync(db.Agent.AgentKey, "新工作", db.Admin, db.Project.Id, null));
        Assert.False(await new TaskProgressService(db.Context).CanUpdateAsync(task.Id, db.Member.Id));
        var notifications = new UserNotificationService(db.Context);
        await notifications.GenerateTaskRemindersAsync(AppTime.Now);
        Assert.Empty(await db.Context.UserNotifications.ToListAsync());
        var cards = await new PersonalActionService(db.Context, new ApprovalRequestService(db.Context, null!, null!, null!)).GetAsync(db.Admin, AppTime.Now);
        Assert.DoesNotContain(cards, c => c.Key == $"task:{task.Id}");
    }
}

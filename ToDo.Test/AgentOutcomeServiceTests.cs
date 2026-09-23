using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.Options;
using ToDo.Entities;

namespace ToDo.Test;

public sealed class AgentOutcomeServiceTests
{
    [Fact]
    public async Task CreateDelivery_UsesSystemEvidenceAndRedactsRawToolPayload()
    {
        await using var db = await OutcomeDatabase.CreateAsync();
        var signing = new IntegritySigningService(Options.Create(new IntegritySigningOptions
        {
            CurrentKeyId = "test-v1",
            CurrentKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray())
        }));
        var service = new AgentOutcomeService(db.Context, signing);

        var receipt = await service.CreateDeliveryAsync(
            db.WorkItem,
            db.Task,
            db.Session,
            "已完成任务检查并写入可核验反馈。");
        var duplicate = await service.CreateDeliveryAsync(
            db.WorkItem,
            db.Task,
            db.Session,
            "重复提交不应覆盖首份交付。");
        await db.Context.SaveChangesAsync();

        Assert.Same(receipt, duplicate);
        Assert.Equal(64, receipt.ContentHash.Length);
        Assert.True(AgentOutcomeService.VerifyContentHash(receipt));
        Assert.Equal("test-v1", receipt.SignatureKeyId);
        Assert.Equal(64, receipt.ContentSignature.Length);
        Assert.True(service.VerifyAuthenticity(receipt));
        Assert.Equal(AgentEvidenceQuality.Strong, receipt.EvidenceQuality);
        Assert.Equal(AgentDeliveryAcceptanceStatus.PendingReview, receipt.AcceptanceStatus);
        Assert.Contains("SelectedTask", receipt.EvidenceJson);
        Assert.Contains("task.add_comment", receipt.ToolEffectsJson);
        Assert.Contains("commentId=42", receipt.ToolEffectsJson);
        Assert.DoesNotContain("top-secret-token", receipt.ToolEffectsJson);
        Assert.DoesNotContain("原始敏感评论正文", receipt.ToolEffectsJson);
        Assert.Single(await db.Context.AgentDeliveryReceipts.ToListAsync());
        var submitted = Assert.Single(await db.Context.AgentPerformanceSignals.ToListAsync());
        Assert.Equal(AgentPerformanceEventType.WorkSubmitted, submitted.EventType);
        Assert.Equal(2, submitted.ScoreDelta);

        receipt.OutcomeSummary = "被篡改的结果";
        Assert.False(AgentOutcomeService.VerifyContentHash(receipt));
        Assert.False(service.VerifyAuthenticity(receipt));
    }

    [Fact]
    public async Task HumanReview_UpdatesReceiptAndWritesOneIdempotentStrongSignal()
    {
        await using var db = await OutcomeDatabase.CreateAsync();
        var service = new AgentOutcomeService(db.Context);
        var receipt = await service.CreateDeliveryAsync(db.WorkItem, db.Task, db.Session, "等待验收");
        await db.Context.SaveChangesAsync();

        var reviewed = await service.ApplyHumanReviewAsync(db.Task.Id, false, db.Admin.Id, "证据不足，请补充测试结果");
        await db.Context.SaveChangesAsync();
        var repeated = await service.ApplyHumanReviewAsync(db.Task.Id, false, db.Admin.Id, "重复提交");
        await db.Context.SaveChangesAsync();

        Assert.Same(receipt, reviewed);
        Assert.Null(repeated);
        Assert.Equal(AgentDeliveryAcceptanceStatus.Rejected, receipt.AcceptanceStatus);
        Assert.Equal(db.Admin.Id, receipt.ReviewedByUserId);
        Assert.Equal("证据不足，请补充测试结果", receipt.ReviewComment);
        var signals = await db.Context.AgentPerformanceSignals.OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(2, signals.Count);
        Assert.Equal(AgentPerformanceEventType.HumanRejected, signals[1].EventType);
        Assert.Equal(-10, signals[1].ScoreDelta);
    }

    [Fact]
    public async Task NewDelivery_SupersedesOlderPendingReceiptForSameTask()
    {
        await using var db = await OutcomeDatabase.CreateAsync();
        var service = new AgentOutcomeService(db.Context);
        var first = await service.CreateDeliveryAsync(db.WorkItem, db.Task, db.Session, "第一版");
        await db.Context.SaveChangesAsync();

        var secondSession = new AiSession
        {
            AgentKey = db.Agent.AgentKey,
            UserId = db.Admin.Id,
            ProjectId = db.Project.Id,
            TaskId = db.Task.Id,
            AgentVersion = db.Agent.Version,
            Status = AiSessionStatus.Succeeded,
            Prompt = "返工",
            Response = "第二版",
            CompletedAt = AppTime.Now
        };
        db.Context.AiSessions.Add(secondSession);
        var secondWorkItem = new AgentWorkItem
        {
            AgentDefinitionId = db.Agent.Id,
            ProjectId = db.Project.Id,
            TaskId = db.Task.Id,
            RequestedByUserId = db.Admin.Id,
            AiSession = secondSession,
            IdempotencyKey = "outcome-second",
            CauseChainId = Guid.NewGuid().ToString("N"),
            Status = AgentWorkItemStatus.Completed
        };
        db.Context.AgentWorkItems.Add(secondWorkItem);
        await db.Context.SaveChangesAsync();

        var second = await service.CreateDeliveryAsync(secondWorkItem, db.Task, secondSession, "第二版");
        await db.Context.SaveChangesAsync();

        Assert.Equal(AgentDeliveryAcceptanceStatus.Superseded, first.AcceptanceStatus);
        Assert.Equal(AgentDeliveryAcceptanceStatus.PendingReview, second.AcceptanceStatus);
        Assert.Equal(2, await db.Context.AgentDeliveryReceipts.CountAsync());
    }

    [Fact]
    public async Task AgendaApproval_UpdatesReceiptAuditAndQueueTogether_WithoutDuplicateReview()
    {
        await using var db = await OutcomeDatabase.CreateAsync();
        var receipt = await new AgentOutcomeService(db.Context).CreateDeliveryAsync(db.WorkItem, db.Task, db.Session, "等待验收");
        db.Task.Status = ToDo.Entities.TaskStatus.PendingConfirmation;
        db.Task.IsCompleted = false;
        db.Task.AssigneeType = TaskAssigneeType.DigitalEmployee;
        db.WorkItem.Status = AgentWorkItemStatus.Retrying;
        await db.Context.SaveChangesAsync();

        var service = new TaskReviewService(db.Context);
        Assert.True(await service.StageAgendaApprovalAsync(db.Task, db.Admin, "会议人工验收通过"));
        await db.Context.SaveChangesAsync();
        Assert.True(await service.StageAgendaApprovalAsync(db.Task, db.Admin, "重复请求"));
        await db.Context.SaveChangesAsync();

        Assert.Equal(ToDo.Entities.TaskStatus.Completed, db.Task.Status);
        Assert.Equal(AgentTaskExecutionStatus.Completed, db.Task.AgentExecutionStatus);
        Assert.Equal(AgentDeliveryAcceptanceStatus.Accepted, receipt.AcceptanceStatus);
        Assert.Equal(db.Admin.Id, receipt.ReviewedByUserId);
        Assert.Equal(AgentWorkItemStatus.Cancelled, db.WorkItem.Status);
        Assert.Single(await db.Context.ChangeLogs.Where(l => l.TaskId == db.Task.Id).ToListAsync());
        Assert.Single(await db.Context.AgentPerformanceSignals.Where(s => s.EventType == AgentPerformanceEventType.HumanAccepted).ToListAsync());
    }

    [Fact]
    public async Task AgendaApproval_RejectsOrdinaryMemberAndCreatorAssignedAsReviewer()
    {
        await using var db = await OutcomeDatabase.CreateAsync();
        var member = new ApplicationUser { UserName = "ordinary", Role = UserRole.teamMember };
        db.Context.Users.Add(member);
        await db.Context.SaveChangesAsync();
        db.Context.ProjectUsers.Add(new ProjectUser { UserId = member.Id, ProjectId = db.Project.Id, ProjectRole = (int)ProjectRole.Member });
        db.Task.Status = ToDo.Entities.TaskStatus.PendingConfirmation;
        db.Task.CreatorId = member.Id;
        db.Task.ReviewerId = db.Admin.Id;
        await db.Context.SaveChangesAsync();
        var service = new TaskReviewService(db.Context);
        Assert.False(await service.StageAgendaApprovalAsync(db.Task, member, "成员绕过审核"));
        db.Task.ReviewerId = member.Id;
        await db.Context.SaveChangesAsync();
        Assert.False(await service.StageAgendaApprovalAsync(db.Task, member, "自行验收"));
        Assert.Equal(ToDo.Entities.TaskStatus.PendingConfirmation, db.Task.Status);
        Assert.Empty(await db.Context.ChangeLogs.ToListAsync());
    }

    internal sealed class OutcomeDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private OutcomeDatabase(
            SqliteConnection connection,
            ApplicationDbContext context,
            ApplicationUser admin,
            Project project,
            AgentDefinition agent,
            ToDoTask task,
            AiSession session,
            AgentWorkItem workItem)
        {
            _connection = connection;
            Context = context;
            Admin = admin;
            Project = project;
            Agent = agent;
            Task = task;
            Session = session;
            WorkItem = workItem;
        }

        public ApplicationDbContext Context { get; }
        public ApplicationUser Admin { get; }
        public Project Project { get; }
        public AgentDefinition Agent { get; }
        public ToDoTask Task { get; }
        public AiSession Session { get; }
        public AgentWorkItem WorkItem { get; }

        public static async Task<OutcomeDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();

            var admin = new ApplicationUser
            {
                UserName = "outcome-admin",
                NormalizedUserName = "OUTCOME-ADMIN",
                RealName = "验收管理员",
                Role = UserRole.systemAdmin,
                Status = UserStatus.Active
            };
            context.Users.Add(admin);
            await context.SaveChangesAsync();
            var project = new Project { Name = "证据测试项目", CreatedByUserId = admin.Id, LeaderUserId = admin.Id };
            context.Project.Add(project);
            var agent = new AgentDefinition
            {
                AgentKey = "evidence-agent",
                Name = "证据 Agent",
                SystemPrompt = "测试",
                IsEnabled = true,
                LifecycleStatus = AgentLifecycleStatus.Published,
                Version = 3
            };
            context.AgentDefinitions.Add(agent);
            await context.SaveChangesAsync();
            var task = new ToDoTask
            {
                Title = "验证 Agent 正式交付",
                CreatorId = admin.Id,
                ReviewerId = admin.Id,
                ProjectId = project.Id,
                AssigneeType = TaskAssigneeType.DigitalEmployee,
                AgentDefinitionId = agent.Id,
                Status = ToDo.Entities.TaskStatus.PendingConfirmation,
                AgentExecutionStatus = AgentTaskExecutionStatus.AwaitingConfirmation
            };
            context.ToDoTasks.Add(task);
            await context.SaveChangesAsync();
            var session = new AiSession
            {
                AgentKey = agent.AgentKey,
                UserId = admin.Id,
                ProjectId = project.Id,
                TaskId = task.Id,
                AgentVersion = agent.Version,
                Status = AiSessionStatus.Succeeded,
                Prompt = "执行",
                Response = "完成",
                CompletedAt = AppTime.Now
            };
            context.AiSessions.Add(session);
            await context.SaveChangesAsync();
            context.AgentToolCalls.AddRange(
                new AgentToolCall
                {
                    AiSessionId = session.Id,
                    ToolName = AgentContextService.ContextReadToolName,
                    IdempotencyKey = "context-read-test",
                    ArgumentsJson = "{\"sources\":[\"Project\",\"SelectedTask\"]}",
                    ResultJson = "{\"secret\":\"top-secret-token\"}",
                    Status = AgentToolCallStatus.Executed,
                    RiskLevel = AgentRiskLevel.Low,
                    ReviewMode = AgentToolReviewMode.Direct
                },
                new AgentToolCall
                {
                    AiSessionId = session.Id,
                    ToolName = "task.add_comment",
                    IdempotencyKey = "comment-tool-test",
                    ArgumentsJson = "{\"content\":\"原始敏感评论正文\"}",
                    ResultJson = "{\"commentId\":42,\"message\":\"写入成功\",\"secret\":\"top-secret-token\",\"content\":\"原始敏感评论正文\"}",
                    Status = AgentToolCallStatus.Executed,
                    RiskLevel = AgentRiskLevel.Medium,
                    ReviewMode = AgentToolReviewMode.HumanApproval
                });
            var workItem = new AgentWorkItem
            {
                AgentDefinitionId = agent.Id,
                ProjectId = project.Id,
                TaskId = task.Id,
                RequestedByUserId = admin.Id,
                AiSessionId = session.Id,
                IdempotencyKey = "outcome-first",
                CauseChainId = Guid.NewGuid().ToString("N"),
                Status = AgentWorkItemStatus.Completed
            };
            context.AgentWorkItems.Add(workItem);
            await context.SaveChangesAsync();
            return new OutcomeDatabase(connection, context, admin, project, agent, task, session, workItem);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}

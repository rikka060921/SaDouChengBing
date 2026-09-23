using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ToDo.Entities;
using ToDo.Entities.DailySummary;

namespace ToDo.Context
{
    public class ApplicationDbContext : IdentityDbContext<
        ApplicationUser,                   // 用户实体
        IdentityRole<int>,                 // 角色实体（int主键）
        int,                                // 主键类型
        IdentityUserClaim<int>,            // 用户声明
        IdentityUserRole<int>,             // 用户角色关联
        IdentityUserLogin<int>,            // 用户登录
        IdentityRoleClaim<int>,            // 角色声明
        IdentityUserToken<int>             // 用户令牌
    >
    {
        // 运行时构造函数
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options) { }

        // DbSet 定义
        public DbSet<DailyReport> DailyReport { get; set; }
        public DbSet<MeetingMinutes> MeetingMinutes { get; set; }
        public DbSet<Project> Project { get; set; }  // 注意：这是 DbSet<Project>，不是 Projects
        public DbSet<TaskGroup> TaskGroups { get; set; }
        public DbSet<ToDoTask> ToDoTasks { get; set; }
        public DbSet<TaskSplitSubmission> TaskSplitSubmissions { get; set; }
        public new DbSet<ApplicationUser> Users { get; set; }
        public DbSet<ChangeLog> ChangeLogs { get; set; }
        public DbSet<ProjectUser> ProjectUsers { get; set; }
        public DbSet<ApplicationUser> ApplicationUser { get; set; }
        public DbSet<MeetingAttachment> MeetingAttachments { get; set; }
        public DbSet<DailyReportAttachment> DailyReportAttachments { get; set; }
        // 新增：仅注册【需入库持久化】的每日汇总实体（3个）
        public DbSet<DailyWorkSummary> DailyWorkSummaries { get; set; }
        public DbSet<DailyProjectSummaryDetail> DailyProjectSummaryDetails { get; set; }
        public DbSet<ProjectRelationSummary> ProjectRelationSummaries { get; set; }
        public DbSet<TaskComment> TaskComments { get; set; }
        public DbSet<TaskLabel> TaskLabels { get; set; }
        public DbSet<TaskLabelLink> TaskLabelLinks { get; set; }
        public DbSet<UserNotification> UserNotifications { get; set; }
        public DbSet<MeetingVersion> MeetingVersions { get; set; }
        public DbSet<MeetingActionItem> MeetingActionItems { get; set; }
        public DbSet<MeetingActionSupervisionEvent> MeetingActionSupervisionEvents { get; set; }
        public DbSet<AgentDefinition> AgentDefinitions { get; set; }
        public DbSet<AgentDefinitionVersion> AgentDefinitionVersions { get; set; }
        public DbSet<AgentAcceptanceContract> AgentAcceptanceContracts { get; set; }
        public DbSet<AgentTestRun> AgentTestRuns { get; set; }
        public DbSet<AgentWorkItem> AgentWorkItems { get; set; }
        public DbSet<AgentDeliveryReceipt> AgentDeliveryReceipts { get; set; }
        public DbSet<AgentAcceptanceRecommendation> AgentAcceptanceRecommendations { get; set; }
        public DbSet<AgentPerformanceSignal> AgentPerformanceSignals { get; set; }
        public DbSet<AgentDispatchDecision> AgentDispatchDecisions { get; set; }
        public DbSet<AgentEventSubscription> AgentEventSubscriptions { get; set; }
        public DbSet<AgentEventExecution> AgentEventExecutions { get; set; }
        public DbSet<AgentRunJob> AgentRunJobs { get; set; }
        public DbSet<AiSession> AiSessions { get; set; }
        public DbSet<DataRetentionRun> DataRetentionRuns { get; set; }
        public DbSet<BackgroundJobLease> BackgroundJobLeases { get; set; }
        public DbSet<EventBusMessage> EventBusMessages { get; set; }
        public DbSet<RedBlueSession> RedBlueSessions { get; set; }
        public DbSet<RedBlueRound> RedBlueRounds { get; set; }
        public DbSet<ScheduledJob> ScheduledJobs { get; set; }
        public DbSet<ProjectDocument> ProjectDocuments { get; set; }
        public DbSet<ProjectDocumentChunk> ProjectDocumentChunks { get; set; }
        public DbSet<IntegrationCallRecord> IntegrationCallRecords { get; set; }
        public DbSet<ApprovalRequest> ApprovalRequests { get; set; }
        public DbSet<AiSessionMessage> AiSessionMessages { get; set; }
        public DbSet<AgentToolCall> AgentToolCalls { get; set; }
        public DbSet<AgentDocumentPermission> AgentDocumentPermissions { get; set; }
        public DbSet<AgentDocumentAccessLog> AgentDocumentAccessLogs { get; set; }
        public DbSet<AgentToolPermission> AgentToolPermissions { get; set; }
        public DbSet<DocumentCategory> DocumentCategories { get; set; }
        public DbSet<AiDocumentDraft> AiDocumentDrafts { get; set; }
        public DbSet<ProjectActivityRecord> ProjectActivityRecords { get; set; }
        public DbSet<ProjectSummaryCheckpoint> ProjectSummaryCheckpoints { get; set; }
        public DbSet<UserSummaryCheckpoint> UserSummaryCheckpoints { get; set; }
        public DbSet<MeetingAgenda> MeetingAgendas { get; set; }
        public DbSet<MeetingAgendaRelation> MeetingAgendaRelations { get; set; }
        public DbSet<MeetingPrepDraft> MeetingPrepDrafts { get; set; }
        public DbSet<MeetingMinutesProject> MeetingMinutesProjects { get; set; }

        // 注意：这里不应该有 object Projects { get; set; } 和 Projects 方法
        // 应该使用 Project 属性来访问项目数据

        // 配置关系
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // 1. MeetingMinutes ↔ Project（多对一）
            modelBuilder.Entity<MeetingMinutes>()
                .HasOne(mm => mm.Project)
                .WithMany(p => p.MeetingMinutes)
                .HasForeignKey(mm => mm.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            // MeetingMinutes ↔ Project 多对多（通过 MeetingMinutesProject）
            modelBuilder.Entity<MeetingMinutesProject>()
                .HasKey(x => new { x.MeetingMinutesId, x.ProjectId });

            modelBuilder.Entity<MeetingMinutesProject>()
                .HasOne(x => x.MeetingMinutes)
                .WithMany(m => m.MeetingProjects)
                .HasForeignKey(x => x.MeetingMinutesId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MeetingMinutesProject>()
                .HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);

            // MeetingActionItem ↔ Project（可空）
            modelBuilder.Entity<MeetingActionItem>()
                .HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);

            // MeetingMinutes ↔ MeetingPrepDraft（多对一，可空，草稿被软删除时不影响纪要）
            modelBuilder.Entity<MeetingMinutes>()
                .HasOne<MeetingPrepDraft>()
                .WithMany()
                .HasForeignKey(mm => mm.PrepDraftId)
                .OnDelete(DeleteBehavior.Restrict);

            // 配置会议纪要与附件的关系（一对多）
            modelBuilder.Entity<MeetingAttachment>()
                .HasOne(ma => ma.MeetingMinutes)
                .WithMany(mm => mm.Attachments)
                .HasForeignKey(ma => ma.MeetingMinutesId)
                .HasConstraintName("FK_MeetingAttachments_MeetingMinutes_Id");

            // 2. DailyReport ↔ Project（多对一）
            modelBuilder.Entity<DailyReport>()
                .HasOne(dr => dr.Project)
                .WithMany(p => p.DailyReports)
                .HasForeignKey(dr => dr.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            // 3. DailyReport ↔ ApplicationUser（多对一）
            modelBuilder.Entity<DailyReport>()
                .HasOne(dr => dr.Reporter)
                .WithMany(u => u.ReportedDailyReports)
                .HasForeignKey(dr => dr.ReporterId)
                .OnDelete(DeleteBehavior.Restrict);

            // 配置日报与附件的一对多关系
            modelBuilder.Entity<DailyReportAttachment>()
                .HasOne(dra => dra.DailyReport)
                .WithMany(dr => dr.Attachments)
                .HasForeignKey(dra => dra.DailyReportId)
                .HasConstraintName("FK_DailyReportAttachments_DailyReport_Id");

            // 4. Task ↔ TaskGroup（多对一）
            modelBuilder.Entity<ToDoTask>()
                .HasOne(t => t.Group)
                .WithMany(g => g.Tasks)
                .HasForeignKey(t => t.GroupId)
                .OnDelete(DeleteBehavior.SetNull);

            // 5. ToDoTask ↔ 关联用户（创建人、指派人、责任人）
            modelBuilder.Entity<ToDoTask>()
                .HasOne(t => t.Assignee)
                .WithMany(u => u.AssignedTasks)
                .HasForeignKey(t => t.AssigneeId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ToDoTask>()
                .HasOne(t => t.Claimer)
                .WithMany(u => u.ClaimedTasks)
                .HasForeignKey(t => t.ClaimerId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ToDoTask>()
                .HasOne(t => t.Creator)
                .WithMany(u => u.CreatedTasks)
                .HasForeignKey(t => t.CreatorId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ToDoTask>()
                .HasOne(t => t.ParentTask)
                .WithMany(t => t.SubTasks)
                .HasForeignKey(t => t.ParentTaskId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<ToDoTask>()
                .HasOne(t => t.Reviewer)
                .WithMany()
                .HasForeignKey(t => t.ReviewerId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<TaskComment>()
                .HasOne(c => c.Task)
                .WithMany(t => t.Comments)
                .HasForeignKey(c => c.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<TaskComment>()
                .HasOne(c => c.Author)
                .WithMany()
                .HasForeignKey(c => c.AuthorId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<TaskLabelLink>()
                .HasKey(link => new { link.TaskId, link.LabelId });

            modelBuilder.Entity<TaskLabelLink>()
                .HasOne(link => link.Task)
                .WithMany(task => task.LabelLinks)
                .HasForeignKey(link => link.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<TaskLabelLink>()
                .HasOne(link => link.Label)
                .WithMany(label => label.TaskLinks)
                .HasForeignKey(link => link.LabelId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<TaskLabel>()
                .HasOne(label => label.Project)
                .WithMany()
                .HasForeignKey(label => label.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserNotification>()
                .HasOne(notification => notification.User)
                .WithMany()
                .HasForeignKey(notification => notification.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MeetingVersion>()
                .HasOne(version => version.MeetingMinutes)
                .WithMany(meeting => meeting.Versions)
                .HasForeignKey(version => version.MeetingMinutesId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MeetingVersion>()
                .HasOne(version => version.Editor)
                .WithMany()
                .HasForeignKey(version => version.EditorId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<MeetingActionItem>()
                .HasOne(item => item.MeetingMinutes)
                .WithMany(meeting => meeting.ActionItems)
                .HasForeignKey(item => item.MeetingMinutesId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MeetingActionItem>()
                .HasOne(item => item.Assignee)
                .WithMany()
                .HasForeignKey(item => item.AssigneeId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<MeetingActionItem>()
                .HasOne(item => item.MatchedTask)
                .WithMany()
                .HasForeignKey(item => item.MatchedTaskId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<MeetingActionSupervisionEvent>()
                .HasOne(item => item.ActionItem)
                .WithMany(item => item.SupervisionEvents)
                .HasForeignKey(item => item.ActionItemId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MeetingActionSupervisionEvent>()
                .HasIndex(item => item.EventKey)
                .IsUnique();

            modelBuilder.Entity<MeetingActionSupervisionEvent>()
                .HasIndex(item => new { item.ProjectId, item.CreatedAt });

            modelBuilder.Entity<MeetingActionSupervisionEvent>()
                .HasIndex(item => new { item.ActionItemId, item.CreatedAt });

            modelBuilder.Entity<RedBlueSession>()
                .HasMany(session => session.Rounds)
                .WithOne()
                .HasForeignKey(round => round.RedBlueSessionId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ProjectDocument>()
                .HasOne<Project>()
                .WithMany()
                .HasForeignKey(document => document.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ProjectDocument>()
                .Property(document => document.Category)
                .HasDefaultValue(ProjectDocumentCategory.Other)
                .ValueGeneratedNever();

            modelBuilder.Entity<ProjectDocument>()
                .Property(document => document.FileHash)
                .IsRequired(false);

            modelBuilder.Entity<ProjectDocument>()
                .Property(document => document.Description)
                .IsRequired(false);

            modelBuilder.Entity<ProjectDocument>()
                .HasMany(document => document.Chunks)
                .WithOne(chunk => chunk.Document)
                .HasForeignKey(chunk => chunk.ProjectDocumentId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ProjectDocumentChunk>()
                .HasIndex(chunk => new { chunk.ProjectDocumentId, chunk.ChunkIndex })
                .IsUnique();

            modelBuilder.Entity<ProjectDocumentChunk>()
                .HasIndex(chunk => new { chunk.ProjectId, chunk.ProjectDocumentId });

            modelBuilder.Entity<IntegrationCallRecord>()
                .HasIndex(call => new { call.Provider, call.IdempotencyKey });

            modelBuilder.Entity<IntegrationCallRecord>()
                .HasIndex(call => call.CreatedAt);

            modelBuilder.Entity<ScheduledJob>()
                .HasIndex(job => new { job.IsEnabled, job.IsRunning, job.NextRunAt });

            modelBuilder.Entity<AiDocumentDraft>()
                .HasOne(d => d.Project)
                .WithMany()
                .HasForeignKey(d => d.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AiDocumentDraft>()
                .HasOne(d => d.SourceDocument)
                .WithMany()
                .HasForeignKey(d => d.SourceDocumentId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AiDocumentDraft>()
                .Property(d => d.OriginalContent)
                .IsRequired(false);

            modelBuilder.Entity<AiDocumentDraft>()
                .Property(d => d.RevisedContent)
                .IsRequired(false);

            modelBuilder.Entity<AiDocumentDraft>()
                .Property(d => d.AuditUserId)
                .IsRequired(false);

            modelBuilder.Entity<AiDocumentDraft>()
                .HasIndex(d => new { d.ProjectId, d.AuditStatus });

            modelBuilder.Entity<DocumentCategory>()
                .HasOne<Project>()
                .WithMany()
                .HasForeignKey(c => c.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<DocumentCategory>()
                .Property(c => c.Name)
                .IsRequired()
                .HasMaxLength(50);

            modelBuilder.Entity<DocumentCategory>()
                .HasIndex(c => new { c.ProjectId, c.Name })
                .IsUnique();

            modelBuilder.Entity<AiSessionMessage>()
                .HasOne(item => item.Session)
                .WithMany(session => session.Messages)
                .HasForeignKey(item => item.AiSessionId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AgentToolCall>()
                .HasOne(item => item.Session)
                .WithMany(session => session.ToolCalls)
                .HasForeignKey(item => item.AiSessionId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AgentToolCall>()
                .HasOne(item => item.ApprovalRequest)
                .WithMany()
                .HasForeignKey(item => item.ApprovalRequestId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<AgentDefinition>()
                .HasIndex(item => item.AgentKey)
                .IsUnique();

            modelBuilder.Entity<ToDoTask>()
                .HasOne(task => task.AgentDefinition)
                .WithMany(agent => agent.AssignedTasks)
                .HasForeignKey(task => task.AgentDefinitionId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<ToDoTask>()
                .Property(task => task.ConcurrencyVersion)
                .IsConcurrencyToken();

            modelBuilder.Entity<AgentWorkItem>()
                .HasOne(item => item.AgentDefinition)
                .WithMany(agent => agent.WorkItems)
                .HasForeignKey(item => item.AgentDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AgentWorkItem>()
                .HasOne(item => item.Project)
                .WithMany()
                .HasForeignKey(item => item.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AgentWorkItem>()
                .HasOne(item => item.Task)
                .WithMany(task => task.AgentWorkItems)
                .HasForeignKey(item => item.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AgentWorkItem>()
                .HasOne(item => item.RequestedByUser)
                .WithMany()
                .HasForeignKey(item => item.RequestedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AgentWorkItem>()
                .HasOne(item => item.AiSession)
                .WithMany()
                .HasForeignKey(item => item.AiSessionId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<AgentWorkItem>()
                .HasOne(item => item.WaitingApprovalRequest)
                .WithMany()
                .HasForeignKey(item => item.WaitingApprovalRequestId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<AgentWorkItem>()
                .HasIndex(item => item.IdempotencyKey)
                .IsUnique();

            modelBuilder.Entity<AgentWorkItem>()
                .HasIndex(item => new { item.Status, item.NextRunAt });

            modelBuilder.Entity<AgentWorkItem>()
                .HasIndex(item => new { item.TaskId, item.CreatedAt });

            modelBuilder.Entity<AgentDeliveryReceipt>()
                .HasOne(item => item.AgentWorkItem)
                .WithOne(item => item.DeliveryReceipt)
                .HasForeignKey<AgentDeliveryReceipt>(item => item.AgentWorkItemId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<AgentDeliveryReceipt>()
                .HasIndex(item => item.AgentWorkItemId)
                .IsUnique();
            modelBuilder.Entity<AgentDeliveryReceipt>()
                .HasOne(item => item.AgentDefinition)
                .WithMany()
                .HasForeignKey(item => item.AgentDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<AgentDeliveryReceipt>()
                .HasOne(item => item.Project)
                .WithMany()
                .HasForeignKey(item => item.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<AgentDeliveryReceipt>()
                .HasOne(item => item.Task)
                .WithMany()
                .HasForeignKey(item => item.TaskId)
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<AgentDeliveryReceipt>()
                .HasOne(item => item.AiSession)
                .WithMany()
                .HasForeignKey(item => item.AiSessionId)
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<AgentDeliveryReceipt>()
                .HasOne(item => item.ReviewedByUser)
                .WithMany()
                .HasForeignKey(item => item.ReviewedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<AgentDeliveryReceipt>()
                .HasIndex(item => new { item.TaskId, item.CreatedAt });

            modelBuilder.Entity<AgentAcceptanceRecommendation>()
                .HasOne(item => item.DeliveryReceipt).WithMany().HasForeignKey(item => item.DeliveryReceiptId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<AgentAcceptanceRecommendation>()
                .HasOne(item => item.AgentRunJob).WithMany().HasForeignKey(item => item.AgentRunJobId).OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<AgentAcceptanceRecommendation>()
                .HasOne(item => item.RequestedByUser).WithMany().HasForeignKey(item => item.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<AgentAcceptanceRecommendation>()
                .HasIndex(item => item.AiSessionId).IsUnique();
            modelBuilder.Entity<AgentAcceptanceRecommendation>()
                .HasIndex(item => item.AgentRunJobId).IsUnique();
            modelBuilder.Entity<AgentAcceptanceRecommendation>()
                .HasIndex(item => new { item.DeliveryReceiptId, item.CreatedAt });

            modelBuilder.Entity<AgentPerformanceSignal>()
                .HasIndex(item => item.EventKey)
                .IsUnique();
            modelBuilder.Entity<AgentPerformanceSignal>()
                .HasIndex(item => new { item.AgentDefinitionId, item.CreatedAt });
            modelBuilder.Entity<AgentPerformanceSignal>()
                .HasOne(item => item.AgentDefinition).WithMany().HasForeignKey(item => item.AgentDefinitionId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<AgentPerformanceSignal>()
                .HasOne(item => item.Project).WithMany().HasForeignKey(item => item.ProjectId).OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<AgentPerformanceSignal>()
                .HasOne(item => item.Task).WithMany().HasForeignKey(item => item.TaskId).OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<AgentPerformanceSignal>()
                .HasOne(item => item.AgentWorkItem).WithMany().HasForeignKey(item => item.AgentWorkItemId).OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<AgentPerformanceSignal>()
                .HasOne(item => item.DeliveryReceipt).WithMany().HasForeignKey(item => item.DeliveryReceiptId).OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<AgentPerformanceSignal>()
                .HasOne(item => item.DispatchDecision).WithMany().HasForeignKey(item => item.DispatchDecisionId).OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<AgentEventSubscription>()
                .HasOne(item => item.AgentDefinition)
                .WithMany()
                .HasForeignKey(item => item.AgentDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AgentEventSubscription>()
                .HasOne(item => item.Project)
                .WithMany()
                .HasForeignKey(item => item.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<AgentDispatchDecision>()
                .HasIndex(item => new { item.TaskId, item.DispatchVersion })
                .IsUnique();

            modelBuilder.Entity<AgentDispatchDecision>()
                .HasOne(item => item.Task)
                .WithMany()
                .HasForeignKey(item => item.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AgentDispatchDecision>()
                .HasOne(item => item.Project)
                .WithMany()
                .HasForeignKey(item => item.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AgentDispatchDecision>()
                .HasOne(item => item.RequestedByUser)
                .WithMany()
                .HasForeignKey(item => item.RequestedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AgentDispatchDecision>()
                .HasOne(item => item.RecommendedAgentDefinition)
                .WithMany(agent => agent.DispatchRecommendations)
                .HasForeignKey(item => item.RecommendedAgentDefinitionId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<AgentDispatchDecision>()
                .HasOne(item => item.SelectedAgentDefinition)
                .WithMany()
                .HasForeignKey(item => item.SelectedAgentDefinitionId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<AgentEventSubscription>()
                .HasOne(item => item.CreatedByUser)
                .WithMany()
                .HasForeignKey(item => item.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AgentEventSubscription>()
                .HasIndex(item => new { item.EventType, item.IsEnabled });

            modelBuilder.Entity<AgentEventExecution>()
                .HasOne(item => item.Subscription)
                .WithMany(rule => rule.Executions)
                .HasForeignKey(item => item.SubscriptionId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AgentEventExecution>()
                .HasOne(item => item.EventBusMessage)
                .WithMany()
                .HasForeignKey(item => item.EventBusMessageId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AgentEventExecution>()
                .HasOne(item => item.AgentDefinition)
                .WithMany()
                .HasForeignKey(item => item.AgentDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AgentEventExecution>()
                .HasOne(item => item.Project)
                .WithMany()
                .HasForeignKey(item => item.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<AgentEventExecution>()
                .HasOne(item => item.Task)
                .WithMany()
                .HasForeignKey(item => item.TaskId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<AgentEventExecution>()
                .HasOne(item => item.RequestedByUser)
                .WithMany()
                .HasForeignKey(item => item.RequestedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AgentEventExecution>()
                .HasOne(item => item.AiSession)
                .WithMany()
                .HasForeignKey(item => item.AiSessionId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<AgentEventExecution>()
                .HasOne(item => item.WaitingApprovalRequest)
                .WithMany()
                .HasForeignKey(item => item.WaitingApprovalRequestId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<AgentEventExecution>()
                .HasIndex(item => new { item.SubscriptionId, item.EventBusMessageId })
                .IsUnique();

            modelBuilder.Entity<AgentEventExecution>()
                .HasIndex(item => new { item.Status, item.NextRunAt });

            modelBuilder.Entity<AgentRunJob>()
                .HasOne(item => item.AiSession).WithMany().HasForeignKey(item => item.AiSessionId).OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<AgentRunJob>()
                .HasOne(item => item.AgentDefinition).WithMany().HasForeignKey(item => item.AgentDefinitionId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<AgentRunJob>()
                .HasOne(item => item.RequestedByUser).WithMany().HasForeignKey(item => item.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<AgentRunJob>()
                .HasOne(item => item.Project).WithMany().HasForeignKey(item => item.ProjectId).OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<AgentRunJob>()
                .HasOne(item => item.Task).WithMany().HasForeignKey(item => item.TaskId).OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<AgentRunJob>()
                .HasOne(item => item.DeliveryReceipt).WithMany().HasForeignKey(item => item.DeliveryReceiptId).OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<AgentRunJob>()
                .HasOne(item => item.WaitingApprovalRequest).WithMany().HasForeignKey(item => item.WaitingApprovalRequestId).OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<AgentRunJob>()
                .HasIndex(item => new { item.Status, item.NextRunAt });
            modelBuilder.Entity<AgentRunJob>()
                .HasIndex(item => new { item.AiSessionId, item.CreatedAt });
            modelBuilder.Entity<AgentRunJob>()
                .HasIndex(item => item.DeliveryReceiptId);

            modelBuilder.Entity<AiSession>()
                .Property(item => item.ConcurrencyVersion)
                .IsConcurrencyToken();
            modelBuilder.Entity<AiSession>()
                .HasIndex(item => item.ContextDeliveryReceiptId);
            modelBuilder.Entity<AiSession>()
                .HasIndex(item => new { item.ArchivedAt, item.LastActivityAt });

            modelBuilder.Entity<DataRetentionRun>()
                .HasOne(item => item.TriggeredByUser)
                .WithMany()
                .HasForeignKey(item => item.TriggeredByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<DataRetentionRun>()
                .HasIndex(item => item.StartedAt);

            modelBuilder.Entity<BackgroundJobLease>()
                .HasIndex(item => item.ExpiresAt);
            modelBuilder.Entity<AgentDefinition>()
                .Property(item => item.Version)
                .IsConcurrencyToken();

            modelBuilder.Entity<AgentDefinitionVersion>()
                .HasOne(item => item.AgentDefinition)
                .WithMany(agent => agent.Versions)
                .HasForeignKey(item => item.AgentDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<AgentDefinitionVersion>()
                .HasOne(item => item.ChangedByUser)
                .WithMany()
                .HasForeignKey(item => item.ChangedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<AgentDefinitionVersion>()
                .HasIndex(item => new { item.AgentDefinitionId, item.Version })
                .IsUnique();

            modelBuilder.Entity<AgentAcceptanceContract>()
                .HasOne(item => item.AgentDefinition)
                .WithOne(agent => agent.AcceptanceContract)
                .HasForeignKey<AgentAcceptanceContract>(item => item.AgentDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<AgentAcceptanceContract>()
                .HasIndex(item => item.AgentDefinitionId)
                .IsUnique();

            modelBuilder.Entity<AgentTestRun>()
                .HasOne(item => item.AgentDefinition)
                .WithMany(agent => agent.TestRuns)
                .HasForeignKey(item => item.AgentDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<AgentTestRun>()
                .HasOne(item => item.RequestedByUser)
                .WithMany()
                .HasForeignKey(item => item.RequestedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<AgentTestRun>()
                .HasOne(item => item.Project)
                .WithMany()
                .HasForeignKey(item => item.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<AgentTestRun>()
                .HasOne(item => item.Task)
                .WithMany()
                .HasForeignKey(item => item.TaskId)
                .OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<AgentTestRun>()
                .HasOne(item => item.AiSession)
                .WithMany()
                .HasForeignKey(item => item.AiSessionId)
                .OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<AgentTestRun>()
                .HasIndex(item => new { item.AgentDefinitionId, item.StartedAt });

            modelBuilder.Entity<AgentToolPermission>()
                .HasOne(item => item.AgentDefinition)
                .WithMany(agent => agent.ToolPermissions)
                .HasForeignKey(item => item.AgentDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AgentToolPermission>()
                .HasIndex(item => new { item.AgentDefinitionId, item.ToolName })
                .IsUnique();

            modelBuilder.Entity<AgentToolCall>()
                .HasIndex(item => item.IdempotencyKey)
                .IsUnique();

            modelBuilder.Entity<AgentDocumentPermission>()
                .HasOne(item => item.Project)
                .WithMany()
                .HasForeignKey(item => item.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<AgentDocumentPermission>()
                .HasOne(item => item.CustomCategory)
                .WithMany()
                .HasForeignKey(item => item.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<AgentDocumentPermission>()
                .HasIndex(item => new { item.ProjectId, item.AgentKey, item.Category, item.CategoryId })
                .IsUnique()
                .HasDatabaseName("IX_agent_doc_perm_proj_agent_cat_catid");

            modelBuilder.Entity<AgentDocumentAccessLog>()
                .HasIndex(item => new { item.ProjectId, item.AgentKey, item.CreatedAt });

            modelBuilder.Entity<ApprovalRequest>()
                .HasOne(item => item.Project)
                .WithMany()
                .HasForeignKey(item => item.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ApprovalRequest>()
                .HasOne(item => item.RequestedBy)
                .WithMany()
                .HasForeignKey(item => item.RequestedById)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ApprovalRequest>()
                .HasOne(item => item.ReviewedBy)
                .WithMany()
                .HasForeignKey(item => item.ReviewedById)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<ProjectActivityRecord>()
                .HasOne(item => item.Project)
                .WithMany()
                .HasForeignKey(item => item.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ProjectActivityRecord>()
                .HasIndex(item => new { item.ProjectId, item.OccurredAt });

            modelBuilder.Entity<ProjectActivityRecord>()
                .HasIndex(item => new { item.EntityType, item.EntityId });

            modelBuilder.Entity<ProjectSummaryCheckpoint>()
                .HasOne(item => item.Project)
                .WithMany()
                .HasForeignKey(item => item.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ProjectSummaryCheckpoint>()
                .HasOne(item => item.LastDailyReport)
                .WithMany()
                .HasForeignKey(item => item.LastDailyReportId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<ProjectSummaryCheckpoint>()
                .HasIndex(item => item.ProjectId)
                .IsUnique();

            modelBuilder.Entity<ProjectSummaryCheckpoint>()
                .Property(item => item.Version)
                .IsConcurrencyToken();

            modelBuilder.Entity<DailyWorkSummary>()
                .HasOne(item => item.User)
                .WithMany()
                .HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<DailyWorkSummary>()
                .HasIndex(item => new { item.UserId, item.SummaryDate })
                .IsUnique();

            modelBuilder.Entity<UserSummaryCheckpoint>()
                .HasOne(item => item.User)
                .WithMany()
                .HasForeignKey(item => item.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserSummaryCheckpoint>()
                .HasOne(item => item.LastDailyWorkSummary)
                .WithMany()
                .HasForeignKey(item => item.LastDailyWorkSummaryId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<UserSummaryCheckpoint>()
                .HasIndex(item => item.UserId)
                .IsUnique();

            modelBuilder.Entity<UserSummaryCheckpoint>()
                .Property(item => item.Version)
                .IsConcurrencyToken();
            modelBuilder.Entity<MeetingAgenda>()
                .HasOne(ma => ma.Project)
                .WithMany()
                .HasForeignKey(ma => ma.ProjectId)
              .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MeetingAgenda>()
                            .HasIndex(ma => new { ma.ProjectId, ma.IsDeleted, ma.SortOrder });

            modelBuilder.Entity<MeetingAgenda>()
                .HasOne(ma => ma.Project)
                .WithMany()
                .HasForeignKey(ma => ma.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MeetingAgenda>()
                .HasIndex(ma => new { ma.ProjectId, ma.IsDeleted, ma.SortOrder });

            modelBuilder.Entity<MeetingAgendaRelation>()
                .HasOne(r => r.MeetingMinutes)
                .WithMany(mm => mm.AgendaRelations)
                .HasForeignKey(r => r.MeetingMinutesId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MeetingAgendaRelation>()
                .HasOne(r => r.MeetingAgenda)
                .WithMany(ma => ma.MeetingRelations)
                .HasForeignKey(r => r.MeetingAgendaId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<MeetingAgendaRelation>()
                .HasIndex(r => new { r.MeetingMinutesId, r.MeetingAgendaId })
                .IsUnique();

            // 会前准备草稿
            modelBuilder.Entity<MeetingPrepDraft>(e =>
            {
                e.HasOne(d => d.Creator)
                    .WithMany()
                    .HasForeignKey(d => d.CreatorId)
                    .OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(d => new { d.CreatorId, d.IsDeleted, d.CreatedAt });
                e.Property(d => d.SelectedProjectIdsJson).IsRequired();
                e.Property(d => d.SelectedTaskIdsJson).IsRequired();
            });

            // 6. Project ↔ User（创建人+成员）
            modelBuilder.Entity<ProjectUser>()
                .HasKey(pu => new { pu.ProjectId, pu.UserId });

            modelBuilder.Entity<Project>()
                .HasOne(p => p.CreatedByUser)
                .WithMany(u => u.CreatedProjects)
                .HasForeignKey(p => p.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ProjectUser>()
                .HasOne(pu => pu.Project)
                .WithMany(p => p.ProjectUsers)
                .HasForeignKey(pu => pu.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ProjectUser>()
                .HasOne(pu => pu.User)
                .WithMany(u => u.ProjectUsers)
                .HasForeignKey(pu => pu.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            // 7. ChangeLog ↔ Project/Task
            modelBuilder.Entity<ChangeLog>()
                .HasOne(c => c.Project)
                .WithMany(p => p.ChangeLogs)
                .HasForeignKey(c => c.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<ChangeLog>()
                .HasOne(c => c.Task)
                .WithMany(t => t.ChangeLogs)
                .HasForeignKey(c => c.TaskId)
                .OnDelete(DeleteBehavior.SetNull);



            // 8. 配置用户实体额外属性
            modelBuilder.Entity<ApplicationUser>(entity =>
            {
                entity.Property(u => u.RealName).HasMaxLength(100);
                entity.Property(u => u.IdentityNumber).HasMaxLength(20);
                entity.Property(u => u.AvatarUrl).HasMaxLength(255);
            });

            // 9. 配置Project与LeaderUser的关系（负责人）
            modelBuilder.Entity<Project>()
                .HasOne(p => p.LeaderUser)
                .WithMany(u => u.LeadedProjects)
                .HasForeignKey(p => p.LeaderUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // 10. 为 ProjectUser 实体添加检查约束，限定 ProjectRole 的取值范围
            modelBuilder.Entity<ProjectUser>().ToTable(table =>
                table.HasCheckConstraint("CK_ProjectUser_ProjectRole", "ProjectRole IN (0, 1)"));
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
            => SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

        public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            // MySQL row locks protect the archive/write boundary until the actual save commits.
            await using var ownedTransaction = Database.CurrentTransaction == null &&
                Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) == true
                ? await Database.BeginTransactionAsync(cancellationToken) : null;
            await ProjectWriteGuard.ValidateAsync(this, cancellationToken);
            PrepareTaskConcurrencyVersions();
            CaptureProjectActivityRecords();
            var count = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (ownedTransaction != null) await ownedTransaction.CommitAsync(cancellationToken);
            return count;
        }

        private void PrepareTaskConcurrencyVersions()
        {
            ChangeTracker.DetectChanges();
            foreach (var entry in ChangeTracker.Entries<ToDoTask>()
                         .Where(item => item.State is EntityState.Added or EntityState.Modified))
            {
                var version = entry.Property(item => item.ConcurrencyVersion);
                if (entry.State == EntityState.Added)
                {
                    if (version.CurrentValue <= 0) version.CurrentValue = 1;
                    continue;
                }

                if (!version.IsModified)
                    version.CurrentValue = version.OriginalValue >= int.MaxValue
                        ? 1
                        : Math.Max(1, version.OriginalValue + 1);
            }
        }

        private void CaptureProjectActivityRecords()
        {
            ChangeTracker.DetectChanges();
            var occurredAt = AppTime.Now;
            var records = new List<ProjectActivityRecord>();

            foreach (var entry in ChangeTracker.Entries<ToDoTask>()
                         .Where(item => item.State is EntityState.Added or EntityState.Modified)
                         .ToList())
            {
                CaptureTaskChanges(entry, records, occurredAt);
            }

            foreach (var entry in ChangeTracker.Entries<MeetingMinutes>()
                         .Where(item => item.State is EntityState.Added or EntityState.Modified)
                         .ToList())
            {
                CaptureMeetingChanges(entry, records, occurredAt);
            }

            foreach (var entry in ChangeTracker.Entries<ProjectDocument>()
                         .Where(item => item.State is EntityState.Added or EntityState.Modified)
                         .ToList())
            {
                CaptureDocumentChanges(entry, records, occurredAt);
            }

            foreach (var entry in ChangeTracker.Entries<Project>()
                         .Where(item => item.State == EntityState.Modified)
                         .ToList())
            {
                CaptureProjectChanges(entry, records, occurredAt);
            }

            if (records.Count > 0)
            {
                ProjectActivityRecords.AddRange(records);
            }
        }

        private static void CaptureTaskChanges(EntityEntry<ToDoTask> entry, ICollection<ProjectActivityRecord> records, DateTime occurredAt)
        {
            var task = entry.Entity;
            if (task.ProjectId <= 0) return;

            if (entry.State == EntityState.Added)
            {
                AddActivity(records, task.ProjectId, ProjectActivityEntityType.Task, task.Id, task.Title,
                    ProjectActivityChangeType.Created, "Task", null, task.Status.ToString(), occurredAt);
                return;
            }

            AddChangedProperty(entry, records, task.ProjectId, task.Id, task.Title, "Status", item => item.Status, occurredAt);
            AddChangedProperty(entry, records, task.ProjectId, task.Id, task.Title, "Deadline", item => item.EndTime, occurredAt, FormatDateTime);
            AddChangedProperty(entry, records, task.ProjectId, task.Id, task.Title, "Progress", item => item.Progress, occurredAt);
            AddChangedProperty(entry, records, task.ProjectId, task.Id, task.Title, "Title", item => item.Title, occurredAt);

            if (entry.Property(item => item.Description).IsModified)
            {
                AddActivity(records, task.ProjectId, ProjectActivityEntityType.Task, task.Id, task.Title,
                    ProjectActivityChangeType.Updated, "Description", "已记录", "已更新", occurredAt);
            }

            if (entry.Property(item => item.AssigneeType).IsModified
                || entry.Property(item => item.AssigneeId).IsModified
                || entry.Property(item => item.AgentName).IsModified
                || entry.Property(item => item.AgentDefinitionId).IsModified)
            {
                var oldValue = FormatAssignee(
                    entry.Property(item => item.AssigneeType).OriginalValue,
                    entry.Property(item => item.AssigneeId).OriginalValue,
                    entry.Property(item => item.AgentName).OriginalValue);
                var newValue = FormatAssignee(task.AssigneeType, task.AssigneeId, task.AgentName);
                if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                {
                    AddActivity(records, task.ProjectId, ProjectActivityEntityType.Task, task.Id, task.Title,
                        ProjectActivityChangeType.Updated, "Assignee", oldValue, newValue, occurredAt);
                }
            }

            if (entry.Property(item => item.IsDeleted).IsModified)
            {
                AddActivity(records, task.ProjectId, ProjectActivityEntityType.Task, task.Id, task.Title,
                    task.IsDeleted ? ProjectActivityChangeType.Deleted : ProjectActivityChangeType.Restored,
                    "IsDeleted", (!task.IsDeleted).ToString(), task.IsDeleted.ToString(), occurredAt);
            }
        }

        private static void CaptureMeetingChanges(EntityEntry<MeetingMinutes> entry, ICollection<ProjectActivityRecord> records, DateTime occurredAt)
        {
            var meeting = entry.Entity;
            if (meeting.ProjectId <= 0) return;

            if (entry.State == EntityState.Added)
            {
                AddActivity(records, meeting.ProjectId, ProjectActivityEntityType.MeetingMinutes, meeting.Id,
                    meeting.MeetingTitle, ProjectActivityChangeType.Created, "MeetingMinutes", null, "已创建", occurredAt);
                return;
            }

            AddChangedProperty(entry, records, meeting.ProjectId, meeting.Id, meeting.MeetingTitle, "Title", item => item.MeetingTitle, occurredAt);
            AddChangedProperty(entry, records, meeting.ProjectId, meeting.Id, meeting.MeetingTitle, "MeetingDate", item => item.MeetingDate, occurredAt, FormatDateTime);

            if (entry.Property(item => item.MeetingContent).IsModified || entry.Property(item => item.TranscriptText).IsModified)
            {
                AddActivity(records, meeting.ProjectId, ProjectActivityEntityType.MeetingMinutes, meeting.Id,
                    meeting.MeetingTitle, ProjectActivityChangeType.Updated, "Content", "已记录", "已更新", occurredAt);
            }

            if (entry.Property(item => item.IsDeleted).IsModified)
            {
                AddActivity(records, meeting.ProjectId, ProjectActivityEntityType.MeetingMinutes, meeting.Id,
                    meeting.MeetingTitle, meeting.IsDeleted ? ProjectActivityChangeType.Deleted : ProjectActivityChangeType.Restored,
                    "IsDeleted", (!meeting.IsDeleted).ToString(), meeting.IsDeleted.ToString(), occurredAt);
            }
        }

        private static void CaptureDocumentChanges(EntityEntry<ProjectDocument> entry, ICollection<ProjectActivityRecord> records, DateTime occurredAt)
        {
            var document = entry.Entity;
            if (document.ProjectId <= 0) return;

            if (entry.State == EntityState.Added)
            {
                AddActivity(records, document.ProjectId, ProjectActivityEntityType.ProjectDocument, document.Id,
                    document.FileName, ProjectActivityChangeType.Created, "Document", null,
                    FormatDocumentCategory(document.Category, document.CategoryId, document.CategoryName), occurredAt);
                return;
            }

            if (entry.Property(item => item.Category).IsModified
                || entry.Property(item => item.CategoryId).IsModified
                || entry.Property(item => item.CategoryName).IsModified)
            {
                var oldValue = FormatDocumentCategory(
                    entry.Property(item => item.Category).OriginalValue,
                    entry.Property(item => item.CategoryId).OriginalValue,
                    entry.Property(item => item.CategoryName).OriginalValue);
                var newValue = FormatDocumentCategory(document.Category, document.CategoryId, document.CategoryName);
                if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                {
                    AddActivity(records, document.ProjectId, ProjectActivityEntityType.ProjectDocument, document.Id,
                        document.FileName, ProjectActivityChangeType.Updated, "Category", oldValue, newValue, occurredAt);
                }
            }

            if (entry.Property(item => item.Description).IsModified
                || entry.Property(item => item.StoragePath).IsModified
                || entry.Property(item => item.FileHash).IsModified
                || entry.Property(item => item.VersionNumber).IsModified
                || entry.Property(item => item.IsCurrent).IsModified)
            {
                AddActivity(records, document.ProjectId, ProjectActivityEntityType.ProjectDocument, document.Id,
                    document.FileName, ProjectActivityChangeType.Updated, "Content", "已记录", "已更新", occurredAt);
            }
        }

        private static void CaptureProjectChanges(EntityEntry<Project> entry, ICollection<ProjectActivityRecord> records, DateTime occurredAt)
        {
            var project = entry.Entity;
            if (project.Id <= 0) return;

            AddChangedProperty(entry, records, project.Id, project.Id, project.Name, "Name", item => item.Name, occurredAt, entityType: ProjectActivityEntityType.Project);
            AddChangedProperty(entry, records, project.Id, project.Id, project.Name, "Status", item => item.Status, occurredAt, entityType: ProjectActivityEntityType.Project);

            if (entry.Property(item => item.Description).IsModified || entry.Property(item => item.Requirements).IsModified)
            {
                AddActivity(records, project.Id, ProjectActivityEntityType.Project, project.Id, project.Name,
                    ProjectActivityChangeType.Updated, "Content", "已记录", "已更新", occurredAt);
            }

            if (entry.Property(item => item.IsDeleted).IsModified)
            {
                AddActivity(records, project.Id, ProjectActivityEntityType.Project, project.Id, project.Name,
                    project.IsDeleted ? ProjectActivityChangeType.Deleted : ProjectActivityChangeType.Restored,
                    "IsDeleted", (!project.IsDeleted).ToString(), project.IsDeleted.ToString(), occurredAt);
            }
        }

        private static void AddChangedProperty<TEntity, TValue>(
            EntityEntry<TEntity> entry,
            ICollection<ProjectActivityRecord> records,
            int projectId,
            int entityId,
            string entityName,
            string fieldName,
            System.Linq.Expressions.Expression<Func<TEntity, TValue>> propertyExpression,
            DateTime occurredAt,
            Func<TValue, string?>? formatter = null,
            ProjectActivityEntityType entityType = ProjectActivityEntityType.Task)
            where TEntity : class
        {
            var property = entry.Property(propertyExpression);
            if (!property.IsModified) return;

            var oldValue = formatter == null ? Convert.ToString(property.OriginalValue) : formatter(property.OriginalValue);
            var newValue = formatter == null ? Convert.ToString(property.CurrentValue) : formatter(property.CurrentValue);
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) return;

            AddActivity(records, projectId, entityType, entityId, entityName,
                ProjectActivityChangeType.Updated, fieldName, oldValue, newValue, occurredAt);
        }

        private static void AddActivity(
            ICollection<ProjectActivityRecord> records,
            int projectId,
            ProjectActivityEntityType entityType,
            int entityId,
            string entityName,
            ProjectActivityChangeType changeType,
            string fieldName,
            string? oldValue,
            string? newValue,
            DateTime occurredAt)
        {
            records.Add(new ProjectActivityRecord
            {
                ProjectId = projectId,
                EntityType = entityType,
                EntityId = entityId > 0 ? entityId : null,
                EntityName = Limit(entityName, 255) ?? "未命名",
                ChangeType = changeType,
                FieldName = fieldName,
                OldValue = Limit(oldValue, 2000),
                NewValue = Limit(newValue, 2000),
                OccurredAt = occurredAt
            });
        }

        private static string FormatAssignee(TaskAssigneeType type, int? assigneeId, string? agentName)
        {
            return type == TaskAssigneeType.DigitalEmployee
                ? $"agent:{agentName?.Trim() ?? string.Empty}"
                : assigneeId.HasValue ? $"human:{assigneeId.Value}" : "none";
        }

        private static string FormatDocumentCategory(ProjectDocumentCategory category, int? categoryId, string? categoryName)
        {
            return categoryId.HasValue
                ? $"custom:{categoryId.Value}:{categoryName?.Trim() ?? string.Empty}"
                : $"preset:{category}:{category.GetDisplayName()}";
        }

        private static string? FormatDateTime(DateTime? value) => value?.ToString("O");
        private static string FormatDateTime(DateTime value) => value.ToString("O");
        private static string? Limit(string? value, int maxLength) => string.IsNullOrEmpty(value) ? value : value[..Math.Min(value.Length, maxLength)];
    }
}

using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public class ApprovalRequestService
{
    public const string LegacyPayloadHash = "legacy-unverified";
    public const string RedBlueDecisionDocument = "RedBlueDecisionDocument";
    public const string AgentTaskComment = "AgentTaskComment";
    public const string AgentTaskUpdate = "AgentTaskUpdate";
    public const string AgentTaskCreate = "AgentTaskCreate";
    public const string AgentDocumentWrite = "AgentDocumentWrite";
    public const string AgentProjectUpdate = "AgentProjectUpdate";
    public const string AgentMeetingActionCreate = "AgentMeetingActionCreate";
    public const string AgentDailyReportCreate = "AgentDailyReportCreate";

    private readonly ApplicationDbContext _context;
    private readonly ProjectDocumentService _documents;
    private readonly UserNotificationService _notifications;
    private readonly AgentDocumentAccessService _documentAccess;
    private readonly IntegritySigningService? _signing;

    public ApprovalRequestService(
        ApplicationDbContext context,
        ProjectDocumentService documents,
        UserNotificationService notifications,
        AgentDocumentAccessService documentAccess,
        IntegritySigningService? signing = null)
    {
        _context = context;
        _documents = documents;
        _notifications = notifications;
        _documentAccess = documentAccess;
        _signing = signing;
    }

    public async Task<ApprovalRequest> RequestAsync(
        int projectId,
        int requestedById,
        string sourceType,
        int? sourceId,
        string actionType,
        string summary,
        string payloadJson,
        CancellationToken cancellationToken = default,
        AgentRiskLevel riskLevel = AgentRiskLevel.High,
        AgentToolReviewMode reviewMode = AgentToolReviewMode.HumanApproval,
        string policyName = "",
        bool notifyApprovers = true)
    {
        var requester = await _context.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == requestedById, cancellationToken)
            ?? throw new InvalidOperationException("申请人不存在");
        if (!await CanAccessProjectAsync(projectId, requester, cancellationToken))
            throw new UnauthorizedAccessException("你不是该项目成员，不能发起写入申请");
        if (payloadJson.Length > 1_000_000)
            throw new InvalidOperationException("审批载荷超过 1 MB 安全上限");

        var existing = await _context.ApprovalRequests
            .FirstOrDefaultAsync(item => item.ProjectId == projectId
                && item.SourceType == sourceType
                && item.SourceId == sourceId
                && item.ActionType == actionType
                && item.Status == ApprovalRequestStatus.Pending, cancellationToken);
        if (existing != null) return existing;

        var request = new ApprovalRequest
        {
            ProjectId = projectId,
            RequestedById = requestedById,
            SourceType = sourceType,
            SourceId = sourceId,
            ActionType = actionType,
            Summary = summary.Length > 500 ? summary[..500] : summary,
            PayloadJson = payloadJson,
            PayloadHash = ComputePayloadHash(
                projectId,
                requestedById,
                sourceType,
                sourceId,
                actionType,
                payloadJson,
                riskLevel,
                policyName),
            RiskLevel = riskLevel,
            ReviewMode = reviewMode,
            PolicyName = policyName,
            Status = ApprovalRequestStatus.Pending
        };
        if (_signing?.IsConfigured == true)
        {
            request.PayloadSignature = _signing.SignHash(request.PayloadHash);
            request.SignatureKeyId = _signing.CurrentKeyId;
        }
        else
        {
            request.SignatureKeyId = "unsigned-development";
        }
        _context.ApprovalRequests.Add(request);
        await _context.SaveChangesAsync(cancellationToken);

        if (notifyApprovers)
            await NotifyApproversAsync(request, cancellationToken);
        return request;
    }

    public async Task ResolveAutomatedReviewAsync(
        int id,
        AgentAutomatedReviewResult review,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _context.ApprovalRequests.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("审核请求不存在");
        if (snapshot.Status != ApprovalRequestStatus.Pending) return;
        EnsurePayloadIntegrity(snapshot);

        var reviewJson = JsonSerializer.Serialize(new
        {
            decision = review.Decision.ToString(),
            review.Reason,
            raw = review.RawResponse
        });

        if (review.Decision == AgentAutomatedReviewDecision.Escalate)
        {
            await _context.ApprovalRequests
                .Where(item => item.Id == id && item.Status == ApprovalRequestStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ReviewMode, AgentToolReviewMode.HumanApproval)
                    .SetProperty(item => item.ReviewComment, Truncate(review.Reason, 1000))
                    .SetProperty(item => item.AutomatedReviewJson, reviewJson), cancellationToken);
            var request = await _context.ApprovalRequests.AsNoTracking().FirstAsync(item => item.Id == id, cancellationToken);
            await NotifyApproversAsync(request, cancellationToken);
            await AddAutomatedToolMessageAsync(request, $"AI 审核无法确定，已升级人工：{review.Reason}", cancellationToken);
            return;
        }

        if (review.Decision == AgentAutomatedReviewDecision.Reject)
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            var reviewedAt = AppTime.Now;
            var affected = await _context.ApprovalRequests
                .Where(item => item.Id == id && item.Status == ApprovalRequestStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, ApprovalRequestStatus.Rejected)
                    .SetProperty(item => item.ReviewedAt, reviewedAt)
                    .SetProperty(item => item.ReviewComment, Truncate(review.Reason, 1000))
                    .SetProperty(item => item.ExecutionResult, "AI 审核拒绝，未执行")
                    .SetProperty(item => item.AutomatedReviewJson, reviewJson), cancellationToken);
            if (affected != 1) throw new InvalidOperationException("该审核请求已被其他执行器处理");
            await UpdateToolCallAsync(snapshot, AgentToolCallStatus.Rejected, $"AI 审核拒绝：{review.Reason}", cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await _notifications.NotifyAsync(snapshot.RequestedById, "Agent 操作被 AI 审核拒绝", snapshot.Summary, "Warning", "/AiSessions/Index");
            return;
        }

        var claimed = false;
        await using var approveTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var reviewedAt = AppTime.Now;
            var affected = await _context.ApprovalRequests
                .Where(item => item.Id == id && item.Status == ApprovalRequestStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, ApprovalRequestStatus.Processing)
                    .SetProperty(item => item.ReviewedAt, reviewedAt)
                    .SetProperty(item => item.ReviewComment, Truncate(review.Reason, 1000))
                    .SetProperty(item => item.AutomatedReviewJson, reviewJson), cancellationToken);
            if (affected != 1) throw new InvalidOperationException("该审核请求已被其他执行器处理");
            claimed = true;
            var request = await _context.ApprovalRequests.FirstAsync(item => item.Id == id, cancellationToken);
            request.ExecutionResult = await ExecuteAsync(request, request.RequestedById, cancellationToken);
            request.Status = ApprovalRequestStatus.Approved;
            await UpdateToolCallAsync(request, AgentToolCallStatus.Executed, $"AI 审核通过并执行：{request.ExecutionResult}", cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await approveTransaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await approveTransaction.RollbackAsync(CancellationToken.None);
            if (claimed)
                await MarkExecutionFailedAsync(id, null, review.Reason, ex.Message, CancellationToken.None);
            throw;
        }
        await _notifications.NotifyAsync(snapshot.RequestedById, "Agent 操作已通过 AI 审核", snapshot.Summary, "Success", "/AiSessions/Index");
    }

    public async Task<List<ApprovalRequest>> GetAccessibleAsync(ApplicationUser user, CancellationToken cancellationToken = default, int? requestId = null)
    {
        var query = _context.ApprovalRequests
            .AsNoTracking()
            .Include(item => item.Project)
            .Include(item => item.RequestedBy)
            .Include(item => item.ReviewedBy)
            .AsQueryable();
        if (user.Role != UserRole.systemAdmin)
        {
            query = query.Where(item => item.RequestedById == user.Id
                || item.Project!.LeaderUserId == user.Id
                || _context.ProjectUsers.Any(member => member.ProjectId == item.ProjectId
                    && member.UserId == user.Id
                    && member.ProjectRole == (int)ProjectRole.Admin));
        }
        if (requestId.HasValue) query = query.Where(item => item.Id == requestId.Value);
        return await query.OrderBy(item => item.Status != ApprovalRequestStatus.Pending)
            .ThenByDescending(item => item.RequestedAt)
            .Take(200)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> CanApproveAsync(int projectId, ApplicationUser user, CancellationToken cancellationToken = default)
    {
        if (user.Role == UserRole.systemAdmin) return true;
        if (await _context.Project.AsNoTracking().AnyAsync(item => item.Id == projectId && item.LeaderUserId == user.Id, cancellationToken)) return true;
        return await _context.ProjectUsers.AsNoTracking().AnyAsync(item => item.ProjectId == projectId
            && item.UserId == user.Id
            && item.ProjectRole == (int)ProjectRole.Admin, cancellationToken);
    }

    public async Task<bool> CanApproveRequestAsync(
        ApprovalRequest request,
        ApplicationUser user,
        CancellationToken cancellationToken = default)
    {
        if (!await CanApproveAsync(request.ProjectId, user, cancellationToken)) return false;
        return !RequiresIndependentApprover(request, user);
    }

    public async Task ApproveAsync(int id, ApplicationUser reviewer, string? comment, CancellationToken cancellationToken = default)
    {
        var snapshot = await _context.ApprovalRequests.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("审批请求不存在");
        if (snapshot.Status != ApprovalRequestStatus.Pending) throw new InvalidOperationException("该请求已处理");
        if (!await CanApproveAsync(snapshot.ProjectId, reviewer, cancellationToken))
            throw new UnauthorizedAccessException("你没有该项目的审批权限");
        if (RequiresIndependentApprover(snapshot, reviewer))
            throw new UnauthorizedAccessException("高风险 Agent 操作不能由申请人本人审批，请交由其他项目管理员、项目负责人或系统管理员处理");
        EnsurePayloadIntegrity(snapshot);

        var reviewComment = Truncate(comment?.Trim() ?? string.Empty, 1000);
        var reviewedAt = AppTime.Now;
        var claimed = false;
        var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var affected = await _context.ApprovalRequests
                .Where(item => item.Id == id && item.Status == ApprovalRequestStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, ApprovalRequestStatus.Processing)
                    .SetProperty(item => item.ReviewedById, reviewer.Id)
                    .SetProperty(item => item.ReviewedAt, reviewedAt)
                    .SetProperty(item => item.ReviewComment, reviewComment), cancellationToken);
            if (affected != 1)
                throw new InvalidOperationException("该审批已被其他人处理，请刷新后查看");

            claimed = true;
            var request = await _context.ApprovalRequests.FirstAsync(item => item.Id == id, cancellationToken);
            request.ExecutionResult = await ExecuteAsync(request, reviewer.Id, cancellationToken);
            request.Status = ApprovalRequestStatus.Approved;
            await UpdateToolCallAsync(request, AgentToolCallStatus.Executed, request.ExecutionResult, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await transaction.DisposeAsync();
            if (claimed)
                await MarkExecutionFailedAsync(id, reviewer.Id, reviewComment, ex.Message, CancellationToken.None);
            throw;
        }
        await transaction.DisposeAsync();
        await _notifications.NotifyAsync(snapshot.RequestedById, "敏感操作已审批通过", snapshot.Summary, "Success", "/Approvals/Index");
    }

    public async Task RejectAsync(int id, ApplicationUser reviewer, string? comment, CancellationToken cancellationToken = default)
    {
        var request = await _context.ApprovalRequests.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("审批请求不存在");
        if (request.Status != ApprovalRequestStatus.Pending) throw new InvalidOperationException("该请求已处理");
        if (!await CanApproveAsync(request.ProjectId, reviewer, cancellationToken))
            throw new UnauthorizedAccessException("你没有该项目的审批权限");

        var reviewComment = Truncate(comment?.Trim() ?? string.Empty, 1000);
        await using (var transaction = await _context.Database.BeginTransactionAsync(cancellationToken))
        {
            var reviewedAt = AppTime.Now;
            var affected = await _context.ApprovalRequests
                .Where(item => item.Id == id && item.Status == ApprovalRequestStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, ApprovalRequestStatus.Rejected)
                    .SetProperty(item => item.ReviewedById, reviewer.Id)
                    .SetProperty(item => item.ReviewedAt, reviewedAt)
                    .SetProperty(item => item.ReviewComment, reviewComment)
                    .SetProperty(item => item.ExecutionResult, "未执行"), cancellationToken);
            if (affected != 1)
                throw new InvalidOperationException("该审批已被其他人处理，请刷新后查看");

            await UpdateToolCallAsync(request, AgentToolCallStatus.Rejected, reviewComment, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await _notifications.NotifyAsync(request.RequestedById, "敏感操作已被驳回", $"{request.Summary}。{reviewComment}", "Warning", "/Approvals/Index");
    }

    private async Task MarkExecutionFailedAsync(
        int id,
        int? reviewerId,
        string reviewComment,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        var safeError = string.IsNullOrWhiteSpace(errorMessage) ? "审批执行失败" : errorMessage.Trim();
        if (safeError.Length > 1000) safeError = safeError[..1000];

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var reviewedAt = AppTime.Now;
        var affected = await _context.ApprovalRequests
            .Where(item => item.Id == id && item.Status == ApprovalRequestStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, ApprovalRequestStatus.Failed)
                .SetProperty(item => item.ReviewedById, reviewerId)
                .SetProperty(item => item.ReviewedAt, reviewedAt)
                .SetProperty(item => item.ReviewComment, reviewComment)
                .SetProperty(item => item.ExecutionResult, safeError), cancellationToken);
        if (affected == 1)
        {
            var request = await _context.ApprovalRequests.AsNoTracking()
                .FirstAsync(item => item.Id == id, cancellationToken);
            await UpdateToolCallAsync(request, AgentToolCallStatus.Failed, safeError, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await transaction.RollbackAsync(cancellationToken);
    }

    private async Task NotifyApproversAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var recipients = new List<int>();
        foreach (var id in await new AttentionRecipientService(_context).ManagersAsync(request.ProjectId, cancellationToken))
        {
            var candidate = await _context.Users.AsNoTracking().SingleAsync(u => u.Id == id, cancellationToken);
            if (!await CanApproveRequestAsync(request, candidate, cancellationToken)) continue;
            recipients.Add(id);
            break;
        }
        await _notifications.NotifyManyAsync(
            recipients,
            "敏感操作待审批",
            request.Summary,
            "Approval",
            $"/Approvals/Index?requestId={request.Id}#approval-{request.Id}");
    }

    private async Task AddAutomatedToolMessageAsync(ApprovalRequest request, string content, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.SourceType, "AgentToolCall", StringComparison.OrdinalIgnoreCase) || !request.SourceId.HasValue) return;
        var call = await _context.AgentToolCalls.AsNoTracking().FirstOrDefaultAsync(item => item.Id == request.SourceId.Value, cancellationToken);
        if (call == null) return;
        _context.AiSessionMessages.Add(new AiSessionMessage
        {
            AiSessionId = call.AiSessionId,
            Role = AiSessionMessageRole.Tool,
            ToolName = call.ToolName,
            Content = content
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length > maxLength ? value[..maxLength] : value;

    private async Task<string> ExecuteAsync(ApprovalRequest request, int reviewerId, CancellationToken cancellationToken)
    {
        EnsurePayloadIntegrity(request);
        await ProjectLifecycleRules.RequireActiveAsync(_context, request.ProjectId, cancellationToken);
        if (request.ActionType == RedBlueDecisionDocument)
        {
            var payload = JsonSerializer.Deserialize<GeneratedDocumentPayload>(request.PayloadJson)
                ?? throw new InvalidOperationException("审批载荷无效");
            var document = await _documents.SaveGeneratedTextAsync(
                request.ProjectId,
                reviewerId,
                payload.FileName,
                payload.Content,
                payload.Description,
                cancellationToken);
            return $"已创建项目资料 #{document.Id}：{document.FileName} v{document.VersionNumber}";
        }
        if (request.ActionType == AgentTaskComment)
        {
            var payload = JsonSerializer.Deserialize<AgentTaskCommentPayload>(request.PayloadJson)
                ?? throw new InvalidOperationException("任务评论审批载荷无效");
            var task = await _context.ToDoTasks.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == payload.TaskId
                    && item.ProjectId == request.ProjectId
                    && !item.IsDeleted, cancellationToken)
                ?? throw new InvalidOperationException("目标任务不存在或已删除");
            if (string.IsNullOrWhiteSpace(payload.Content))
                throw new InvalidOperationException("评论内容不能为空");
            var content = payload.Content.Trim();
            _context.TaskComments.Add(new TaskComment
            {
                TaskId = task.Id,
                AuthorId = request.RequestedById,
                Content = content.Length > 2000 ? content[..2000] : content,
                IsAiGenerated = true,
                AgentKey = payload.AgentKey,
                AiSessionId = payload.AiSessionId
            });
            await _context.SaveChangesAsync(cancellationToken);
            return $"已向任务 #{task.Id} 写入 Agent 评论";
        }
        if (request.ActionType == AgentTaskUpdate)
        {
            var payload = JsonSerializer.Deserialize<AgentTaskUpdatePayload>(request.PayloadJson)
                ?? throw new InvalidOperationException("任务更新审批载荷无效");
            var task = await _context.ToDoTasks.FirstOrDefaultAsync(item => item.Id == payload.TaskId
                && item.ProjectId == request.ProjectId
                && !item.IsDeleted, cancellationToken)
                ?? throw new InvalidOperationException("目标任务不存在或已删除");

            if (!string.IsNullOrWhiteSpace(payload.Status))
            {
                if (!Enum.TryParse<ToDo.Entities.TaskStatus>(payload.Status, true, out var status))
                    throw new InvalidOperationException("任务状态无效");
                task.SetStatus(status);
            }
            if (payload.AssigneeId.HasValue)
            {
                if (!await IsProjectParticipantAsync(request.ProjectId, payload.AssigneeId.Value, cancellationToken))
                    throw new InvalidOperationException("指定负责人不是项目成员");
                task.AssigneeId = payload.AssigneeId;
                task.AssigneeType = TaskAssigneeType.Human;
                task.AgentName = null;
                task.AgentDefinitionId = null;
                task.AgentExecutionStatus = AgentTaskExecutionStatus.None;
                task.AgentAssignmentVersion++;
                task.AgentLastError = string.Empty;
            }
            if (payload.Deadline.HasValue) task.EndTime = payload.Deadline;
            if (!string.IsNullOrWhiteSpace(payload.Description)) task.Description = payload.Description.Trim();
            task.UpdatedAt = AppTime.Now;
            await _context.SaveChangesAsync(cancellationToken);
            return $"已更新任务 #{task.Id}：{task.Title}";
        }
        if (request.ActionType == AgentTaskCreate)
        {
            var payload = JsonSerializer.Deserialize<AgentTaskCreatePayload>(request.PayloadJson)
                ?? throw new InvalidOperationException("任务创建审批载荷无效");
            if (string.IsNullOrWhiteSpace(payload.Title)) throw new InvalidOperationException("任务标题不能为空");
            if (payload.AssigneeId.HasValue
                && !await IsProjectParticipantAsync(request.ProjectId, payload.AssigneeId.Value, cancellationToken))
                throw new InvalidOperationException("指定负责人不是项目成员");
            var projectLeaderId = await _context.Project.Where(item => item.Id == request.ProjectId).Select(item => item.LeaderUserId).FirstOrDefaultAsync(cancellationToken);
            var task = new ToDoTask
            {
                ProjectId = request.ProjectId,
                CreatorId = request.RequestedById,
                Title = payload.Title.Trim().Length > 255 ? payload.Title.Trim()[..255] : payload.Title.Trim(),
                Description = payload.Description?.Trim(),
                AssigneeId = payload.AssigneeId,
                ReviewerId = projectLeaderId > 0 ? projectLeaderId : null,
                EndTime = payload.Deadline,
                Priority = Enum.TryParse<TaskPriority>(payload.Priority, true, out var priority) ? priority : TaskPriority.Medium,
                Status = ToDo.Entities.TaskStatus.NotStarted
            };
            _context.ToDoTasks.Add(task);
            await _context.SaveChangesAsync(cancellationToken);
            return $"已创建任务 #{task.Id}：{task.Title}";
        }
        if (request.ActionType == AgentDocumentWrite)
        {
            var payload = JsonSerializer.Deserialize<AgentDocumentWritePayload>(request.PayloadJson)
                ?? throw new InvalidOperationException("资料写入审批载荷无效");
            var canWrite = await _documentAccess.CanWriteAsync(request.ProjectId, payload.AgentKey, payload.Category, cancellationToken);
            if (!canWrite)
            {
                await _documentAccess.LogWriteAttemptAsync(request.ProjectId, payload.AgentKey, payload.Category, false,
                    "审批执行时分类写入权限已撤销", request.RequestedById, payload.AiSessionId, cancellationToken: cancellationToken);
                throw new UnauthorizedAccessException("Agent 没有该资料分类的写入权限");
            }
            var document = await _documents.SaveGeneratedTextAsync(request.ProjectId, reviewerId, payload.FileName,
                payload.Content, payload.Description, cancellationToken, payload.Category);
            await _documentAccess.LogWriteAttemptAsync(request.ProjectId, payload.AgentKey, payload.Category, true,
                "人工审批通过并完成资料写入", request.RequestedById, payload.AiSessionId, document.Id, cancellationToken);
            return $"已创建项目资料 #{document.Id}：{document.FileName} v{document.VersionNumber}";
        }
        if (request.ActionType == AgentProjectUpdate)
        {
            var payload = JsonSerializer.Deserialize<AgentProjectUpdatePayload>(request.PayloadJson)
                ?? throw new InvalidOperationException("项目修改审批载荷无效");
            var project = await _context.Project.FirstOrDefaultAsync(item => item.Id == request.ProjectId && !item.IsDeleted, cancellationToken)
                ?? throw new InvalidOperationException("目标项目不存在或已删除");
            if (payload.Name != null)
            {
                if (string.IsNullOrWhiteSpace(payload.Name)) throw new InvalidOperationException("项目名称不能为空");
                var name = payload.Name.Trim();
                project.Name = name.Length > 255 ? name[..255] : name;
            }
            if (payload.Description != null)
            {
                var description = payload.Description.Trim();
                project.Description = description.Length > 1000 ? description[..1000] : description;
            }
            if (payload.Requirements != null) project.Requirements = payload.Requirements.Trim();
            if (payload.Status != null)
            {
                if (!Enum.TryParse<ProjectStatus>(payload.Status, true, out var status))
                    throw new InvalidOperationException("项目状态无效，只能是 Active 或 Archived");
                if (status != project.Status)
                    throw new InvalidOperationException("请从项目列表执行归档或恢复，以检查未结束的工作并确认影响。");
            }
            // Project 的 CreatedAt/UpdatedAt 统一按 UTC 保存，显示时再转北京时间。
            project.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return $"已更新项目 #{project.Id}：{project.Name}";
        }
        if (request.ActionType == AgentMeetingActionCreate)
        {
            var payload = JsonSerializer.Deserialize<AgentMeetingActionCreatePayload>(request.PayloadJson)
                ?? throw new InvalidOperationException("会议行动项审批载荷无效");
            var meeting = await _context.MeetingMinutes.AsNoTracking().FirstOrDefaultAsync(item => item.Id == payload.MeetingMinutesId
                && item.ProjectId == request.ProjectId && !item.IsDeleted, cancellationToken)
                ?? throw new InvalidOperationException("目标会议纪要不存在或已删除");
            if (string.IsNullOrWhiteSpace(payload.Content)) throw new InvalidOperationException("会议行动项内容不能为空");
            if (payload.AssigneeId.HasValue && !await IsProjectParticipantAsync(request.ProjectId, payload.AssigneeId.Value, cancellationToken))
                throw new InvalidOperationException("指定负责人不是项目成员");
            if (payload.MatchedTaskId.HasValue && !await _context.ToDoTasks.AsNoTracking().AnyAsync(item => item.Id == payload.MatchedTaskId.Value
                && item.ProjectId == request.ProjectId && !item.IsDeleted, cancellationToken))
                throw new InvalidOperationException("关联任务不存在或不属于当前项目");
            var content = payload.Content.Trim();
            var assigneeText = payload.AssigneeText?.Trim();
            var actionItem = new MeetingActionItem
            {
                MeetingMinutesId = meeting.Id,
                Content = content.Length > 1000 ? content[..1000] : content,
                AssigneeId = payload.AssigneeId,
                AssigneeText = assigneeText?.Length > 100 ? assigneeText[..100] : assigneeText,
                Deadline = payload.Deadline,
                MatchedTaskId = payload.MatchedTaskId,
                SyncStatus = payload.MatchedTaskId.HasValue ? "已关联任务" : "待处理",
                SyncMessage = "由 Agent 提议并经人工审批创建"
            };
            _context.MeetingActionItems.Add(actionItem);
            await _context.SaveChangesAsync(cancellationToken);
            return $"已创建会议行动项 #{actionItem.Id}：{actionItem.Content}";
        }
        if (request.ActionType == AgentDailyReportCreate)
        {
            var payload = JsonSerializer.Deserialize<AgentDailyReportCreatePayload>(request.PayloadJson)
                ?? throw new InvalidOperationException("日报审批载荷无效");
            if (payload.ReportType is < 1 or > 3) throw new InvalidOperationException("报告类型无效");
            if (payload.ReportDate.Date > AppTime.Today) throw new InvalidOperationException("报告日期不能晚于今天");
            if (string.IsNullOrWhiteSpace(payload.Title) || string.IsNullOrWhiteSpace(payload.Content))
                throw new InvalidOperationException("报告标题和内容不能为空");
            var title = payload.Title.Trim();
            var report = new DailyReport
            {
                ProjectId = request.ProjectId,
                ReportType = payload.ReportType,
                ReportDate = payload.ReportDate.Date,
                ReportTitle = title.Length > 200 ? title[..200] : title,
                ReportContent = payload.Content.Trim(),
                ReporterId = request.RequestedById,
                CreatedAt = AppTime.Now,
                LastModifiedAt = AppTime.Now
            };
            _context.DailyReport.Add(report);
            await _context.SaveChangesAsync(cancellationToken);
            return $"已创建{report.GetReportTypeText()} #{report.Id}：{report.ReportTitle}";
        }
        throw new InvalidOperationException($"尚未注册敏感操作执行器：{request.ActionType}");
    }

    private async Task UpdateToolCallAsync(ApprovalRequest request, AgentToolCallStatus status, string result, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.SourceType, "AgentToolCall", StringComparison.OrdinalIgnoreCase) || !request.SourceId.HasValue) return;
        var toolCall = await _context.AgentToolCalls.FirstOrDefaultAsync(item => item.Id == request.SourceId.Value, cancellationToken);
        if (toolCall == null) return;
        toolCall.Status = status;
        toolCall.ResultJson = JsonSerializer.Serialize(new { message = result });
        toolCall.CompletedAt = AppTime.Now;

        var statusText = status switch
        {
            AgentToolCallStatus.Executed => "审批通过并已执行",
            AgentToolCallStatus.Rejected => "审批已驳回",
            AgentToolCallStatus.Failed => "审批执行失败",
            _ => "审批状态已更新"
        };
        _context.AiSessionMessages.Add(new AiSessionMessage
        {
            AiSessionId = toolCall.AiSessionId,
            Role = AiSessionMessageRole.Tool,
            ToolName = toolCall.ToolName,
            Content = $"{statusText}：{(string.IsNullOrWhiteSpace(result) ? "无补充信息" : result)}"
        });
        var session = await _context.AiSessions.FirstOrDefaultAsync(item => item.Id == toolCall.AiSessionId, cancellationToken);
        if (session != null)
        {
            session.LastActivityAt = AppTime.Now;
            session.ConcurrencyVersion++;
        }
    }

    private async Task<bool> CanAccessProjectAsync(int projectId, ApplicationUser user, CancellationToken cancellationToken)
    {
        if (user.Role == UserRole.systemAdmin) return true;
        return await _context.Project.AsNoTracking().AnyAsync(project => project.Id == projectId
            && !project.IsDeleted
            && (project.LeaderUserId == user.Id
                || _context.ProjectUsers.Any(member => member.ProjectId == project.Id && member.UserId == user.Id)), cancellationToken);
    }

    public static bool VerifyPayloadIntegrity(ApprovalRequest request)
    {
        // 迁移时明确标记的历史请求保持可处理；新请求的哈希若被清空则按篡改处理。
        if (string.Equals(request.PayloadHash, LegacyPayloadHash, StringComparison.Ordinal)) return true;
        if (string.IsNullOrWhiteSpace(request.PayloadHash)) return false;
        var expected = ComputePayloadHash(
            request.ProjectId,
            request.RequestedById,
            request.SourceType,
            request.SourceId,
            request.ActionType,
            request.PayloadJson,
            request.RiskLevel,
            request.PolicyName);
        return string.Equals(expected, request.PayloadHash, StringComparison.OrdinalIgnoreCase);
    }

    public bool VerifyPayloadAuthenticity(ApprovalRequest request)
    {
        if (!VerifyPayloadIntegrity(request)) return false;
        if (string.IsNullOrWhiteSpace(request.PayloadSignature))
            return _signing?.IsConfigured != true
                && (request.SignatureKeyId is "legacy-hash-only" or "unsigned-development"
                    || string.Equals(request.PayloadHash, LegacyPayloadHash, StringComparison.Ordinal));
        return _signing?.VerifyHash(request.PayloadHash, request.PayloadSignature, request.SignatureKeyId) == true;
    }

    private void EnsurePayloadIntegrity(ApprovalRequest request)
    {
        if (!VerifyPayloadAuthenticity(request))
            throw new InvalidOperationException("审批载荷签名或完整性校验失败，已拒绝执行；请撤销该请求并重新发起");
    }

    private static bool RequiresIndependentApprover(ApprovalRequest request, ApplicationUser reviewer)
        => reviewer.Role != UserRole.systemAdmin
            && request.RequestedById == reviewer.Id
            && request.RiskLevel == AgentRiskLevel.High
            && string.Equals(request.SourceType, "AgentToolCall", StringComparison.OrdinalIgnoreCase);

    private static string ComputePayloadHash(
        int projectId,
        int requestedById,
        string sourceType,
        int? sourceId,
        string actionType,
        string payloadJson,
        AgentRiskLevel riskLevel,
        string policyName)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            projectId,
            requestedById,
            sourceType,
            sourceId,
            actionType,
            payloadJson,
            riskLevel,
            policyName
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private Task<bool> IsProjectParticipantAsync(int projectId, int userId, CancellationToken cancellationToken)
    {
        return _context.Project.AsNoTracking().AnyAsync(project => project.Id == projectId
            && !project.IsDeleted
            && (project.LeaderUserId == userId
                || _context.ProjectUsers.Any(member => member.ProjectId == projectId && member.UserId == userId)), cancellationToken);
    }

    private async Task<List<int>> GetApproverIdsAsync(int projectId, CancellationToken cancellationToken)
    {
        var ids = await _context.ProjectUsers.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.ProjectRole == (int)ProjectRole.Admin)
            .Select(item => item.UserId)
            .ToListAsync(cancellationToken);
        ids.AddRange(await _context.Users.AsNoTracking()
            .Where(item => item.Role == UserRole.systemAdmin && !item.IsDeleted)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken));
        var leaderId = await _context.Project.AsNoTracking().Where(item => item.Id == projectId).Select(item => item.LeaderUserId).FirstOrDefaultAsync(cancellationToken);
        if (leaderId > 0) ids.Add(leaderId);
        return ids.Distinct().ToList();
    }
}

public sealed class GeneratedDocumentPayload
{
    public string FileName { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

public sealed class AgentTaskCommentPayload
{
    public int TaskId { get; init; }
    public string Content { get; init; } = string.Empty;
    public string AgentKey { get; init; } = string.Empty;
    public int? AiSessionId { get; init; }
}

public sealed class AgentTaskUpdatePayload
{
    public int TaskId { get; init; }
    public string? Status { get; init; }
    public int? AssigneeId { get; init; }
    public DateTime? Deadline { get; init; }
    public string? Description { get; init; }
}

public sealed class AgentTaskCreatePayload
{
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int? AssigneeId { get; init; }
    public DateTime? Deadline { get; init; }
    public string Priority { get; init; } = "Medium";
}

public sealed class AgentDocumentWritePayload
{
    public string AgentKey { get; init; } = string.Empty;
    public int? AiSessionId { get; init; }
    public ProjectDocumentCategory Category { get; init; } = ProjectDocumentCategory.Other;
    public string FileName { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

public sealed class AgentProjectUpdatePayload
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Requirements { get; init; }
    public string? Status { get; init; }
}

public sealed class AgentMeetingActionCreatePayload
{
    public int MeetingMinutesId { get; init; }
    public string Content { get; init; } = string.Empty;
    public int? AssigneeId { get; init; }
    public string? AssigneeText { get; init; }
    public DateTime? Deadline { get; init; }
    public int? MatchedTaskId { get; init; }
}

public sealed class AgentDailyReportCreatePayload
{
    public int ReportType { get; init; } = 1;
    public DateTime ReportDate { get; init; } = AppTime.Today;
    public string Title { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}

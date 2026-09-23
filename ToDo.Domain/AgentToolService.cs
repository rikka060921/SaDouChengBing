using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public class AgentToolService
{
    private const string ActionsStart = "<agent-actions>";
    private const string ActionsEnd = "</agent-actions>";

    private readonly ApplicationDbContext _context;
    private readonly ApprovalRequestService _approvals;
    private readonly AgentDocumentAccessService _documentAccess;
    private readonly AiSessionService _sessions;
    private readonly IEventBus _eventBus;
    private readonly IAgentToolCatalog _toolCatalog;
    private readonly AgentRiskPolicyService _riskPolicy;
    private readonly IAgentRegistry? _registry;
    private readonly IAgentWebSearch? _webSearch;

    public AgentToolService(
        ApplicationDbContext context,
        ApprovalRequestService approvals,
        AgentDocumentAccessService documentAccess,
        AiSessionService sessions,
        IEventBus eventBus,
        IAgentToolCatalog toolCatalog,
        AgentRiskPolicyService riskPolicy,
        IAgentRegistry? registry = null,
        IAgentWebSearch? webSearch = null)
    {
        _context = context;
        _approvals = approvals;
        _documentAccess = documentAccess;
        _sessions = sessions;
        _eventBus = eventBus;
        _toolCatalog = toolCatalog;
        _riskPolicy = riskPolicy;
        _registry = registry;
        _webSearch = webSearch;
    }

    public async Task<AgentActionProcessingResult> ProcessResponseAsync(AiSession session, int userId, string response, CancellationToken cancellationToken = default, bool allowBusinessTools = true, bool allowWebSearch = true)
    {
        var parsed = ParseActions(response);
        return await ProcessActionsAsync(session, userId, parsed.CleanResponse, parsed.Actions.Where(action => string.Equals(action.Tool, "web.search", StringComparison.OrdinalIgnoreCase) ? allowWebSearch : allowBusinessTools).ToList(), cancellationToken);
    }

    public async Task<AgentActionProcessingResult> ProcessActionsAsync(
        AiSession session,
        int userId,
        string cleanResponse,
        IEnumerable<AgentToolAction> actions,
        CancellationToken cancellationToken = default)
    {
        var calls = new List<AgentToolCall>();
        var actionIndex = 0;
        foreach (var action in actions.Take(10))
        {
            var argumentsJson = action.Arguments.ValueKind == JsonValueKind.Undefined ? "{}" : action.Arguments.GetRawText();
            var idempotencyKey = BuildIdempotencyKey(session, action, argumentsJson, actionIndex++);
            var existing = await _context.AgentToolCalls.AsNoTracking()
                .FirstOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existing != null)
            {
                calls.Add(existing);
                continue;
            }
            var call = new AgentToolCall
            {
                AiSessionId = session.Id,
                ToolName = (action.Tool ?? string.Empty).Trim(),
                IdempotencyKey = idempotencyKey,
                ArgumentsJson = argumentsJson,
                Status = AgentToolCallStatus.Proposed
            };
            _context.AgentToolCalls.Add(call);
            await _context.SaveChangesAsync(cancellationToken);
            calls.Add(call);

            try
            {
                await ExecuteOrProposeAsync(session, userId, call, action.Arguments, cancellationToken);
                await _eventBus.PublishAsync("agent.tool.processed", new { call.Id, call.ToolName, call.Status }, "AgentToolCall", call.Id.ToString(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 停机不能把已经落库的成功操作改写成失败；由队列保留原记录恢复。
                // 尚未确认结果的 Proposed 不能在重放时被误当成完成，也不能擅自重执行未知副作用。
                if (call.Status == AgentToolCallStatus.Proposed)
                {
                    call.Status = AgentToolCallStatus.Failed;
                    call.ResultJson = JsonSerializer.Serialize(new { error = "工具执行中断，结果未确认，需核对后再继续" });
                    call.CompletedAt = AppTime.Now;
                    await _context.SaveChangesAsync(CancellationToken.None);
                }
                throw;
            }
            catch (Exception ex)
            {
                call.Status = AgentToolCallStatus.Failed;
                call.ResultJson = JsonSerializer.Serialize(new { error = ex.Message });
                call.CompletedAt = AppTime.Now;
                await _context.SaveChangesAsync(cancellationToken);
                await _sessions.AddToolMessageAsync(session, call.ToolName, $"工具失败：{ex.Message}");
            }
        }
        return new AgentActionProcessingResult(cleanResponse.Trim(), calls);
    }

    public async Task<AgentToolCall> AddCompletionCommentAsync(AiSession session, int userId, string response, CancellationToken cancellationToken = default)
    {
        if (!session.TaskId.HasValue) throw new InvalidOperationException("自动评论 Agent 必须关联任务");
        var content = $"【{session.AgentKey}】\n{response.Trim()}";
        if (content.Length > 2000) content = content[..2000];
        var arguments = JsonSerializer.SerializeToElement(new { taskId = session.TaskId.Value, content });
        var idempotencyKey = Hash($"completion:{session.Id}:{session.TurnCount + 1}:task.add_comment");
        var existing = await _context.AgentToolCalls.FirstOrDefaultAsync(
            item => item.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (existing != null) return existing;
        var call = new AgentToolCall
        {
            AiSessionId = session.Id,
            ToolName = "task.add_comment",
            IdempotencyKey = idempotencyKey,
            ArgumentsJson = arguments.GetRawText(),
            Status = AgentToolCallStatus.Proposed
        };
        _context.AgentToolCalls.Add(call);
        await _context.SaveChangesAsync(cancellationToken);
        // 这是管理员配置的固定完成动作，不属于模型自行输出的工具调用。
        await AddCommentAsync(session, userId, call, arguments, cancellationToken);
        return call;
    }

    private async Task ExecuteOrProposeAsync(AiSession session, int userId, AgentToolCall call, JsonElement arguments, CancellationToken cancellationToken)
    {
        var descriptor = _toolCatalog.Get(call.ToolName)
            ?? throw new InvalidOperationException($"不支持的 Agent 工具：{call.ToolName}");
        call.ToolName = descriptor.ToolName;
        AgentToolPermission? permission;
        if (_registry != null)
        {
            var runtimeDefinition = await _registry.GetForExecutionAsync(
                session.AgentKey,
                session.AgentVersion,
                cancellationToken: cancellationToken);
            permission = runtimeDefinition?.ToolPermissions.FirstOrDefault(item =>
                item.IsEnabled && string.Equals(item.ToolName, descriptor.ToolName, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            permission = await _context.AgentToolPermissions.AsNoTracking()
                .FirstOrDefaultAsync(item =>
                    item.AgentDefinition != null
                    && item.AgentDefinition.AgentKey == session.AgentKey
                    && item.ToolName == descriptor.ToolName
                    && item.IsEnabled,
                    cancellationToken);
        }
        if (permission == null)
            throw new UnauthorizedAccessException($"Agent「{session.AgentKey}」未注册工具权限：{call.ToolName}");
        call.RiskLevel = descriptor.RiskLevel;
        call.ReviewMode = _riskPolicy.ResolveReviewMode(descriptor, permission);
        call.RequiresApproval = call.ReviewMode == AgentToolReviewMode.HumanApproval;

        switch (call.ToolName)
        {
            case "web.search":
                if (call.ReviewMode != AgentToolReviewMode.Direct)
                    throw new InvalidOperationException("联网搜索为独立只读工具；当前配置要求审批，已阻止外发查询，请管理员检查权限");
                if (!await _context.Users.AsNoTracking().AnyAsync(user => user.Id == userId
                    && !user.IsDeleted && user.Status == UserStatus.Active, cancellationToken))
                    throw new UnauthorizedAccessException("用户已停用，搜索请求未发送");
                var projectId = session.ProjectId ?? throw new InvalidOperationException("联网搜索必须关联项目");
                await EnsureProjectAccessAsync(projectId, userId, cancellationToken);
                // 即使旧版本快照保留搜索权限，管理员撤销当前权限也立即阻止外发。
                if (!await _context.AgentToolPermissions.AsNoTracking().AnyAsync(permission =>
                    permission.AgentDefinition != null && permission.AgentDefinition.AgentKey == session.AgentKey
                    && permission.AgentDefinition.IsEnabled && permission.ToolName == "web.search" && permission.IsEnabled, cancellationToken))
                    throw new InvalidOperationException("联网搜索权限已被撤销");
                if (await _context.AgentToolCalls.CountAsync(x => x.AiSessionId == session.Id
                    && x.ToolName == "web.search", cancellationToken) > 3)
                    throw new InvalidOperationException("单次任务最多调用 3 次联网搜索，已停止继续消耗额度");
                if (_webSearch == null) throw new InvalidOperationException("联网搜索服务尚未配置");
                var result = await _webSearch.SearchAsync(GetString(arguments, "query") ?? string.Empty,
                    GetInt(arguments, "maxResults") ?? 3, cancellationToken);
                call.Status = AgentToolCallStatus.Executed;
                call.RequiresApproval = false;
                call.ResultJson = JsonSerializer.Serialize(result);
                call.CompletedAt = AppTime.Now;
                await _context.SaveChangesAsync(cancellationToken);
                await _sessions.AddToolMessageAsync(session, call.ToolName, call.ResultJson);
                break;
            case "task.add_comment":
                if (call.ReviewMode != AgentToolReviewMode.Direct)
                    await ProposeTaskCommentAsync(session, userId, call, arguments, cancellationToken);
                else
                    await AddCommentAsync(session, userId, call, arguments, cancellationToken);
                return;
            case "task.update":
                await ProposeTaskUpdateAsync(session, userId, call, arguments, cancellationToken);
                return;
            case "task.create":
                await ProposeTaskCreateAsync(session, userId, call, arguments, cancellationToken);
                return;
            case "project.document.write":
                await ProposeDocumentWriteAsync(session, userId, call, arguments, cancellationToken);
                return;
            case "project.update":
                await ProposeProjectUpdateAsync(session, userId, call, arguments, cancellationToken);
                return;
            case "meeting.action.create":
                await ProposeMeetingActionCreateAsync(session, userId, call, arguments, cancellationToken);
                return;
            case "report.create":
                await ProposeDailyReportCreateAsync(session, userId, call, arguments, cancellationToken);
                return;
            default:
                throw new InvalidOperationException($"不支持的 Agent 工具：{call.ToolName}");
        }
    }

    private async Task ProposeTaskCommentAsync(
        AiSession session,
        int userId,
        AgentToolCall call,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var taskId = GetInt(arguments, "taskId") ?? session.TaskId
            ?? throw new InvalidOperationException("缺少 taskId");
        var content = GetString(arguments, "content");
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("评论内容不能为空");
        var task = await _context.ToDoTasks.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == taskId && !item.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("任务不存在或已删除");
        await EnsureProjectAccessAsync(task.ProjectId, userId, cancellationToken);
        EnsureSessionProject(session, task.ProjectId);
        EnsureSessionTask(session, taskId);
        var payload = new AgentTaskCommentPayload
        {
            TaskId = taskId,
            Content = content.Trim().Length > 2000 ? content.Trim()[..2000] : content.Trim(),
            AgentKey = session.AgentKey,
            AiSessionId = session.Id
        };
        await CreateApprovalAsync(
            session,
            userId,
            call,
            task.ProjectId,
            ApprovalRequestService.AgentTaskComment,
            $"Agent「{session.AgentKey}」申请评论任务「{task.Title}」",
            payload,
            cancellationToken);
    }

    private async Task AddCommentAsync(AiSession session, int userId, AgentToolCall call, JsonElement arguments, CancellationToken cancellationToken)
    {
        var taskId = GetInt(arguments, "taskId") ?? session.TaskId
            ?? throw new InvalidOperationException("缺少 taskId");
        var content = GetString(arguments, "content");
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("评论内容不能为空");
        var task = await _context.ToDoTasks.AsNoTracking().FirstOrDefaultAsync(item => item.Id == taskId && !item.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("任务不存在或已删除");
        await EnsureProjectAccessAsync(task.ProjectId, userId, cancellationToken);
        EnsureSessionProject(session, task.ProjectId);
        EnsureSessionTask(session, taskId);

        _context.TaskComments.Add(new TaskComment
        {
            TaskId = task.Id,
            AuthorId = userId,
            Content = content.Trim().Length > 2000 ? content.Trim()[..2000] : content.Trim(),
            IsAiGenerated = true,
            AgentKey = session.AgentKey,
            AiSessionId = session.Id
        });
        call.RequiresApproval = false;
        call.Status = AgentToolCallStatus.Executed;
        call.ResultJson = JsonSerializer.Serialize(new { message = "Agent 评论已写入", taskId });
        call.CompletedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
        await _sessions.AddToolMessageAsync(session, call.ToolName, $"已向任务 #{taskId} 写入 Agent 评论（无需审批）");
    }

    private async Task ProposeTaskUpdateAsync(AiSession session, int userId, AgentToolCall call, JsonElement arguments, CancellationToken cancellationToken)
    {
        var taskId = GetInt(arguments, "taskId") ?? session.TaskId
            ?? throw new InvalidOperationException("缺少 taskId");
        var task = await _context.ToDoTasks.AsNoTracking().FirstOrDefaultAsync(item => item.Id == taskId && !item.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("任务不存在或已删除");
        await EnsureProjectAccessAsync(task.ProjectId, userId, cancellationToken);
        EnsureSessionProject(session, task.ProjectId);
        EnsureSessionTask(session, taskId);
        var status = GetString(arguments, "status")?.Trim();
        if (!string.IsNullOrWhiteSpace(status)
            && !Enum.TryParse<ToDo.Entities.TaskStatus>(status, true, out _))
            throw new InvalidOperationException("任务状态无效");
        var assigneeId = GetInt(arguments, "assigneeId");
        if (assigneeId.HasValue)
            await EnsureProjectParticipantAsync(task.ProjectId, assigneeId.Value, cancellationToken);
        var description = GetString(arguments, "description")?.Trim();
        var payload = new AgentTaskUpdatePayload
        {
            TaskId = taskId,
            Status = status,
            AssigneeId = assigneeId,
            Deadline = GetDate(arguments, "deadline"),
            Description = description == null ? null : description.Length > 20_000 ? description[..20_000] : description
        };
        await CreateApprovalAsync(session, userId, call, task.ProjectId, ApprovalRequestService.AgentTaskUpdate,
            $"Agent「{session.AgentKey}」申请修改任务「{task.Title}」", payload, cancellationToken);
    }

    private async Task ProposeTaskCreateAsync(AiSession session, int userId, AgentToolCall call, JsonElement arguments, CancellationToken cancellationToken)
    {
        var projectId = GetInt(arguments, "projectId") ?? session.ProjectId
            ?? throw new InvalidOperationException("缺少 projectId");
        await EnsureProjectAccessAsync(projectId, userId, cancellationToken);
        EnsureSessionProject(session, projectId);
        var title = GetString(arguments, "title")?.Trim() ?? string.Empty;
        var description = GetString(arguments, "description")?.Trim();
        var assigneeId = GetInt(arguments, "assigneeId");
        if (assigneeId.HasValue)
            await EnsureProjectParticipantAsync(projectId, assigneeId.Value, cancellationToken);
        var priority = GetString(arguments, "priority")?.Trim() ?? "Medium";
        if (!Enum.TryParse<TaskPriority>(priority, true, out _))
            throw new InvalidOperationException("任务优先级必须是 High、Medium 或 Low");
        var payload = new AgentTaskCreatePayload
        {
            Title = title.Length > 255 ? title[..255] : title,
            Description = description == null ? null : description.Length > 20_000 ? description[..20_000] : description,
            AssigneeId = assigneeId,
            Deadline = GetDate(arguments, "deadline"),
            Priority = priority
        };
        if (string.IsNullOrWhiteSpace(payload.Title)) throw new InvalidOperationException("任务标题不能为空");
        await CreateApprovalAsync(session, userId, call, projectId, ApprovalRequestService.AgentTaskCreate,
            $"Agent「{session.AgentKey}」申请创建任务「{payload.Title}」", payload, cancellationToken);
    }

    private async Task ProposeDocumentWriteAsync(AiSession session, int userId, AgentToolCall call, JsonElement arguments, CancellationToken cancellationToken)
    {
        var projectId = GetInt(arguments, "projectId") ?? session.ProjectId
            ?? throw new InvalidOperationException("缺少 projectId");
        await EnsureProjectAccessAsync(projectId, userId, cancellationToken);
        EnsureSessionProject(session, projectId);
        var category = GetCategory(arguments, "category");
        var allowed = await _documentAccess.CanWriteAsync(projectId, session.AgentKey, category, cancellationToken);
        await _documentAccess.LogWriteAttemptAsync(projectId, session.AgentKey, category, allowed,
            allowed ? "已授权，等待人工审批" : "默认拒绝：未配置分类写入权限", userId, session.Id, cancellationToken: cancellationToken);
        if (!allowed) throw new UnauthorizedAccessException($"Agent 没有“{category.GetDisplayName()}”分类的写入权限");

        var payload = new AgentDocumentWritePayload
        {
            AgentKey = session.AgentKey,
            AiSessionId = session.Id,
            Category = category,
            FileName = GetString(arguments, "fileName") ?? $"Agent产物-{session.Id}.md",
            Content = GetString(arguments, "content") ?? string.Empty,
            Description = GetString(arguments, "description") ?? $"由 Agent {session.AgentKey} 生成"
        };
        if (string.IsNullOrWhiteSpace(payload.Content)) throw new InvalidOperationException("资料内容不能为空");
        await CreateApprovalAsync(session, userId, call, projectId, ApprovalRequestService.AgentDocumentWrite,
            $"Agent「{session.AgentKey}」申请写入“{category.GetDisplayName()}”项目资料", payload, cancellationToken);
    }

    private async Task ProposeProjectUpdateAsync(AiSession session, int userId, AgentToolCall call, JsonElement arguments, CancellationToken cancellationToken)
    {
        var projectId = GetInt(arguments, "projectId") ?? session.ProjectId
            ?? throw new InvalidOperationException("缺少 projectId");
        await EnsureProjectAccessAsync(projectId, userId, cancellationToken);
        EnsureSessionProject(session, projectId);
        var project = await _context.Project.AsNoTracking().FirstOrDefaultAsync(item => item.Id == projectId && !item.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("项目不存在或已删除");
        var payload = new AgentProjectUpdatePayload
        {
            Name = GetString(arguments, "name"),
            Description = GetString(arguments, "description"),
            Requirements = GetString(arguments, "requirements"),
            Status = GetString(arguments, "status")
        };
        if (payload.Name == null && payload.Description == null && payload.Requirements == null && payload.Status == null)
            throw new InvalidOperationException("项目修改内容不能为空");
        await CreateApprovalAsync(session, userId, call, projectId, ApprovalRequestService.AgentProjectUpdate,
            $"Agent「{session.AgentKey}」申请修改项目「{project.Name}」", payload, cancellationToken);
    }

    private async Task ProposeMeetingActionCreateAsync(AiSession session, int userId, AgentToolCall call, JsonElement arguments, CancellationToken cancellationToken)
    {
        var meetingId = GetInt(arguments, "meetingId") ?? throw new InvalidOperationException("缺少 meetingId");
        var meeting = await _context.MeetingMinutes.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == meetingId && !item.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("会议纪要不存在或已删除");
        await EnsureProjectAccessAsync(meeting.ProjectId, userId, cancellationToken);
        if (session.ProjectId.HasValue && session.ProjectId.Value != meeting.ProjectId)
            throw new UnauthorizedAccessException("会议纪要不属于当前 Session 项目");
        var payload = new AgentMeetingActionCreatePayload
        {
            MeetingMinutesId = meetingId,
            Content = Truncate(GetString(arguments, "content")?.Trim(), 1000),
            AssigneeId = GetInt(arguments, "assigneeId"),
            AssigneeText = Truncate(GetString(arguments, "assigneeText")?.Trim(), 100),
            Deadline = GetDate(arguments, "deadline"),
            MatchedTaskId = GetInt(arguments, "matchedTaskId")
        };
        if (string.IsNullOrWhiteSpace(payload.Content)) throw new InvalidOperationException("会议行动项内容不能为空");
        if (payload.AssigneeId.HasValue)
            await EnsureProjectParticipantAsync(meeting.ProjectId, payload.AssigneeId.Value, cancellationToken);
        if (payload.MatchedTaskId.HasValue)
        {
            EnsureSessionTask(session, payload.MatchedTaskId.Value);
            var validTask = await _context.ToDoTasks.AsNoTracking().AnyAsync(item => item.Id == payload.MatchedTaskId.Value
                && item.ProjectId == meeting.ProjectId && !item.IsDeleted, cancellationToken);
            if (!validTask) throw new InvalidOperationException("关联任务不存在或不属于当前项目");
        }
        await CreateApprovalAsync(session, userId, call, meeting.ProjectId, ApprovalRequestService.AgentMeetingActionCreate,
            $"Agent「{session.AgentKey}」申请为会议「{meeting.MeetingTitle}」创建行动项", payload, cancellationToken);
    }

    private async Task ProposeDailyReportCreateAsync(AiSession session, int userId, AgentToolCall call, JsonElement arguments, CancellationToken cancellationToken)
    {
        var projectId = GetInt(arguments, "projectId") ?? session.ProjectId
            ?? throw new InvalidOperationException("缺少 projectId");
        await EnsureProjectAccessAsync(projectId, userId, cancellationToken);
        EnsureSessionProject(session, projectId);
        var project = await _context.Project.AsNoTracking().FirstOrDefaultAsync(item => item.Id == projectId && !item.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("项目不存在或已删除");
        var payload = new AgentDailyReportCreatePayload
        {
            ReportType = GetInt(arguments, "reportType") ?? 1,
            ReportDate = GetDate(arguments, "reportDate") ?? AppTime.Today,
            Title = Truncate(GetString(arguments, "title")?.Trim(), 200),
            Content = Truncate(GetString(arguments, "content")?.Trim(), 100_000)
        };
        if (string.IsNullOrWhiteSpace(payload.Title)) throw new InvalidOperationException("日报标题不能为空");
        if (string.IsNullOrWhiteSpace(payload.Content)) throw new InvalidOperationException("日报内容不能为空");
        if (payload.ReportType is < 1 or > 3) throw new InvalidOperationException("报告类型必须是 1（日）、2（周）或 3（月）");
        if (payload.ReportDate.Date > AppTime.Today) throw new InvalidOperationException("报告日期不能晚于今天");
        await CreateApprovalAsync(session, userId, call, projectId, ApprovalRequestService.AgentDailyReportCreate,
            $"Agent「{session.AgentKey}」申请生成项目「{project.Name}」日报", payload, cancellationToken);
    }

    private async Task CreateApprovalAsync<T>(AiSession session, int userId, AgentToolCall call, int projectId, string actionType, string summary, T payload, CancellationToken cancellationToken)
    {
        var descriptor = _toolCatalog.Get(call.ToolName)
            ?? throw new InvalidOperationException($"不支持的 Agent 工具：{call.ToolName}");
        call.RequiresApproval = call.ReviewMode == AgentToolReviewMode.HumanApproval;
        var request = await _approvals.RequestAsync(projectId, userId, "AgentToolCall", call.Id, actionType, summary,
            JsonSerializer.Serialize(payload), cancellationToken,
            call.RiskLevel,
            call.ReviewMode,
            AgentRiskPolicyService.CurrentPolicyName,
            notifyApprovers: call.ReviewMode == AgentToolReviewMode.HumanApproval);
        call.ApprovalRequestId = request.Id;
        call.Status = AgentToolCallStatus.PendingApproval;
        call.ResultJson = JsonSerializer.Serialize(new
        {
            message = call.ReviewMode == AgentToolReviewMode.AiReview ? "等待 AI 自动审核" : "等待人工审批",
            approvalRequestId = request.Id,
            riskLevel = call.RiskLevel.ToString(),
            reviewMode = call.ReviewMode.ToString()
        });
        await _context.SaveChangesAsync(cancellationToken);
        if (call.ReviewMode == AgentToolReviewMode.AiReview)
        {
            await _sessions.AddToolMessageAsync(session, call.ToolName, $"中风险操作已进入独立 AI 审核 #{request.Id}，审核完成前不会写入");
            var review = await _riskPolicy.ReviewAsync(descriptor, summary, request.PayloadJson, cancellationToken);
            call.ReviewReason = review.Reason;
            await _context.SaveChangesAsync(cancellationToken);
            await _approvals.ResolveAutomatedReviewAsync(request.Id, review, cancellationToken);
            return;
        }

        await _sessions.AddToolMessageAsync(session, call.ToolName, $"高风险操作已创建人工审批请求 #{request.Id}，审批前不会执行写入");
    }

    private async Task EnsureProjectAccessAsync(int projectId, int userId, CancellationToken cancellationToken)
    {
        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new UnauthorizedAccessException("用户不存在");
        if (user.Role == UserRole.systemAdmin) return;
        var allowed = await _context.Project.AsNoTracking().AnyAsync(project => project.Id == projectId
            && !project.IsDeleted
            && (project.LeaderUserId == userId
                || _context.ProjectUsers.Any(member => member.ProjectId == projectId && member.UserId == userId)), cancellationToken);
        if (!allowed) throw new UnauthorizedAccessException("当前用户无权操作该项目");
    }

    private static void EnsureSessionProject(AiSession session, int projectId)
    {
        if (session.ProjectId.HasValue && session.ProjectId.Value != projectId)
            throw new UnauthorizedAccessException("目标数据不属于当前 Session 项目");
    }

    private static void EnsureSessionTask(AiSession session, int taskId)
    {
        if (session.TaskId.HasValue && session.TaskId.Value != taskId)
            throw new UnauthorizedAccessException("任务绑定 Session 只能操作当前任务，不能访问同项目的其他任务");
    }

    private async Task EnsureProjectParticipantAsync(int projectId, int userId, CancellationToken cancellationToken)
    {
        var isParticipant = await _context.Project.AsNoTracking().AnyAsync(project => project.Id == projectId
            && !project.IsDeleted
            && (project.LeaderUserId == userId
                || _context.ProjectUsers.Any(member => member.ProjectId == projectId && member.UserId == userId)), cancellationToken);
        if (!isParticipant) throw new InvalidOperationException("指定负责人不是当前项目成员");
    }

    private static string Truncate(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length > maxLength ? text[..maxLength] : text;
    }

    private static ParsedAgentActions ParseActions(string response)
    {
        var start = response.LastIndexOf(ActionsStart, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return new ParsedAgentActions(response.Trim(), new List<AgentToolAction>());
        var end = response.IndexOf(ActionsEnd, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return new ParsedAgentActions(response.Trim(), new List<AgentToolAction>());
        var jsonStart = start + ActionsStart.Length;
        var json = response[jsonStart..end].Trim();
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            List<AgentToolAction> actions;
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                // 兼容旧版协议，历史 Session 仍可继续执行。
                actions = JsonSerializer.Deserialize<List<AgentToolAction>>(json, options) ?? [];
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("actions", out var actionList)
                && actionList.ValueKind == JsonValueKind.Array)
            {
                actions = JsonSerializer.Deserialize<List<AgentToolAction>>(actionList.GetRawText(), options) ?? [];
            }
            else
            {
                return new ParsedAgentActions(response.Trim(), []);
            }
            var clean = (response[..start] + response[(end + ActionsEnd.Length)..]).Trim();
            return new ParsedAgentActions(clean, actions
                .Where(item => !string.IsNullOrWhiteSpace(item.Tool) && item.Arguments.ValueKind == JsonValueKind.Object)
                .ToList());
        }
        catch
        {
            return new ParsedAgentActions(response.Trim(), new List<AgentToolAction>());
        }
    }

    private static string BuildIdempotencyKey(AiSession session, AgentToolAction action, string argumentsJson, int actionIndex)
    {
        var callIdentity = string.IsNullOrWhiteSpace(action.CallId)
            ? $"index:{actionIndex}:{argumentsJson}"
            : $"call:{action.CallId.Trim()}";
        return Hash($"{session.Id}:{session.TurnCount + 1}:{action.Tool.Trim().ToLowerInvariant()}:{callIdentity}");
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : int.TryParse(value.ToString(), out number) ? number : null;
    }

    private static DateTime? GetDate(JsonElement element, string name)
    {
        var value = GetString(element, name);
        return DateTime.TryParse(value, out var date) ? date : null;
    }

    private static ProjectDocumentCategory GetCategory(JsonElement element, string name)
    {
        var value = GetString(element, name)?.Trim();
        if (!string.IsNullOrWhiteSpace(value)
            && Enum.TryParse<ProjectDocumentCategory>(value, true, out var category)
            && Enum.IsDefined(category))
            return category;
        return value switch
        {
            "需求与目标" => ProjectDocumentCategory.RequirementsAndGoals,
            "会议纪要" => ProjectDocumentCategory.MeetingMinutes,
            "任务与计划" => ProjectDocumentCategory.TasksAndPlans,
            "日报与复盘" => ProjectDocumentCategory.DailyReportsAndReviews,
            "技术资料" => ProjectDocumentCategory.TechnicalMaterials,
            "制度与规范" => ProjectDocumentCategory.PoliciesAndStandards,
            "其他" => ProjectDocumentCategory.Other,
            _ => throw new InvalidOperationException($"资料分类无效：{value ?? "未提供"}")
        };
    }
}

public sealed class AgentToolAction
{
    public string CallId { get; set; } = string.Empty;
    public string Tool { get; set; } = string.Empty;
    public JsonElement Arguments { get; set; }
}

public sealed record ParsedAgentActions(string CleanResponse, List<AgentToolAction> Actions);
public sealed record AgentActionProcessingResult(string CleanResponse, List<AgentToolCall> ToolCalls);

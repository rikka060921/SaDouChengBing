using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public sealed class AgentContextService
{
    public const string ContextReadToolName = "context.read";

    private readonly ApplicationDbContext _context;
    private readonly AgentDocumentAccessService _documentAccess;

    public AgentContextService(ApplicationDbContext context, AgentDocumentAccessService documentAccess)
    {
        _context = context;
        _documentAccess = documentAccess;
    }

    public async Task<string> BuildAsync(
        AgentDefinition definition,
        AiSession session,
        int userId,
        CancellationToken cancellationToken = default,
        bool recordAudit = true,
        bool includeInstructions = true)
    {
        var sources = AgentAdministrationService.ParseContextSources(definition.ContextSourcesJson);
        if (definition.RequiresTask && !session.TaskId.HasValue)
            throw new InvalidOperationException("该 Agent 必须关联任务");
        if (definition.RequiresProject && !session.ProjectId.HasValue && !session.TaskId.HasValue)
            throw new InvalidOperationException("该 Agent 必须关联项目");

        ToDoTask? selectedTask = null;
        if (session.TaskId.HasValue)
        {
            selectedTask = await _context.ToDoTasks.AsNoTracking()
                .Include(item => item.Project)
                .Include(item => item.Assignee)
                .Include(item => item.SubTasks.Where(child => !child.IsDeleted))
                .Include(item => item.Comments)
                .FirstOrDefaultAsync(item => item.Id == session.TaskId.Value && !item.IsDeleted, cancellationToken)
                ?? throw new InvalidOperationException("任务不存在或已删除");
            if (AgentSessionExecutionPolicy.IsTaskAssistance(session))
            {
                // 私有协助绑定原项目；校验后任务迁移不能借上下文读取悄悄重绑会话。
                if (session.ProjectId != selectedTask.ProjectId || session.UserId != userId)
                    throw new UnauthorizedAccessException("任务所属项目已变化，请回到任务重新请求 AI 协助");
            }
            else session.ProjectId = selectedTask.ProjectId;
        }

        if (session.ProjectId.HasValue)
            await EnsureProjectAccessAsync(session.ProjectId.Value, userId, cancellationToken);

        var builder = new StringBuilder();
        if (includeInstructions)
            builder.AppendLine(string.IsNullOrWhiteSpace(definition.SystemPrompt)
                ? $"你是系统注册的 Agent：{definition.Name}。职责：{definition.Description}。请直接、准确地完成用户指令。"
                : definition.SystemPrompt.Trim());

        if (sources.Contains(AgentContextSource.Project))
            await AppendProjectAsync(builder, session.ProjectId, cancellationToken);
        if (sources.Contains(AgentContextSource.ProjectTasks))
            await AppendProjectTasksAsync(builder, session.ProjectId, cancellationToken);
        if (sources.Contains(AgentContextSource.SelectedTask))
        {
            AppendSelectedTask(builder, selectedTask);
            await AppendTaskEvidenceAsync(builder, selectedTask, session.ContextDeliveryReceiptId, cancellationToken);
        }
        if (sources.Contains(AgentContextSource.TaskComments))
            AppendTaskComments(builder, selectedTask);
        if (sources.Contains(AgentContextSource.Meetings))
            await AppendMeetingsAsync(builder, session.ProjectId, cancellationToken);
        if (sources.Contains(AgentContextSource.Documents))
            await AppendDocumentsAsync(builder, definition, session, userId, cancellationToken);
        if (sources.Contains(AgentContextSource.Reports))
            await AppendReportsAsync(builder, session.ProjectId, cancellationToken);

        if (recordAudit) await RecordContextReadAsync(session, sources, cancellationToken);
        return builder.ToString().Trim();
    }

    private async Task AppendProjectAsync(StringBuilder builder, int? projectId, CancellationToken cancellationToken)
    {
        if (!projectId.HasValue) throw new InvalidOperationException("项目上下文要求关联项目");
        var project = await _context.Project.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == projectId.Value && !item.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("项目不存在或已删除");
        builder.AppendLine().AppendLine("【项目】")
            .AppendLine($"名称：{project.Name}")
            .AppendLine($"说明：{project.Description}")
            .AppendLine($"目标：{project.Requirements}")
            .AppendLine($"状态：{project.Status}");
    }

    private async Task AppendProjectTasksAsync(StringBuilder builder, int? projectId, CancellationToken cancellationToken)
    {
        if (!projectId.HasValue) throw new InvalidOperationException("任务列表上下文要求关联项目");
        var tasks = await _context.ToDoTasks.AsNoTracking()
            .Where(item => item.ProjectId == projectId.Value && !item.IsDeleted)
            .OrderBy(item => item.Status == ToDo.Entities.TaskStatus.Completed)
            .ThenBy(item => item.EndTime)
            .Take(100)
            .Select(item => new
            {
                item.Id,
                item.Title,
                item.Status,
                item.Progress,
                item.Priority,
                item.EndTime,
                item.AssigneeType,
                item.AgentName,
                AssigneeName = item.Assignee != null ? item.Assignee.RealName : null
            })
            .ToListAsync(cancellationToken);
        builder.AppendLine().AppendLine("【项目任务】");
        if (tasks.Count == 0)
        {
            builder.AppendLine("暂无任务");
            return;
        }
        foreach (var task in tasks)
        {
            var assignee = task.AssigneeType == TaskAssigneeType.DigitalEmployee
                ? task.AgentName ?? "Agent"
                : task.AssigneeName ?? "未分配";
            builder.AppendLine($"- #{task.Id} {task.Title}｜{task.Status}｜{task.Progress}%｜{task.Priority}｜负责人 {assignee}｜截止 {task.EndTime?.ToString("yyyy-MM-dd") ?? "未设置"}");
        }
    }

    private static void AppendSelectedTask(StringBuilder builder, ToDoTask? task)
    {
        if (task == null) throw new InvalidOperationException("任务详情上下文要求关联任务");
        var assignee = task.AssigneeType == TaskAssigneeType.DigitalEmployee
            ? task.AgentName ?? "Agent"
            : task.Assignee?.RealName ?? task.Assignee?.UserName ?? "未分配";
        builder.AppendLine().AppendLine("【当前任务】")
            .AppendLine($"项目：{task.Project?.Name}")
            .AppendLine($"任务：#{task.Id} {task.Title}")
            .AppendLine($"描述：{task.Description}")
            .AppendLine($"状态：{task.Status}，进度：{task.Progress}%，优先级：{task.Priority}")
            .AppendLine($"负责人：{assignee}")
            .AppendLine($"开始：{task.StartTime?.ToString("yyyy-MM-dd") ?? "未设置"}，截止：{task.EndTime?.ToString("yyyy-MM-dd") ?? "未设置"}")
            .AppendLine($"返工次数：{task.ReworkCount}")
            .AppendLine("子任务：");
        if (task.SubTasks.Count == 0)
        {
            builder.AppendLine("- 无");
            return;
        }
        foreach (var child in task.SubTasks.OrderBy(item => item.EndTime))
            builder.AppendLine($"- #{child.Id} {child.Title}｜{child.Status}｜{child.Progress}%｜截止 {child.EndTime?.ToString("yyyy-MM-dd") ?? "未设置"}");
    }

    private static void AppendTaskComments(StringBuilder builder, ToDoTask? task)
    {
        if (task == null) throw new InvalidOperationException("任务评论上下文要求关联任务");
        builder.AppendLine().AppendLine("【任务最新评论】");
        var comments = task.Comments.OrderByDescending(item => item.CreatedAt).Take(20).ToList();
        if (comments.Count == 0)
        {
            builder.AppendLine("暂无评论");
            return;
        }
        foreach (var comment in comments)
            builder.AppendLine($"- {comment.CreatedAt:MM-dd HH:mm} {Truncate(comment.Content, 500)}");
    }

    private async Task AppendTaskEvidenceAsync(
        StringBuilder builder,
        ToDoTask? task,
        long? deliveryReceiptId,
        CancellationToken cancellationToken)
    {
        if (task == null) throw new InvalidOperationException("任务证据上下文要求关联任务");

        var receiptQuery = _context.AgentDeliveryReceipts.AsNoTracking()
            .Where(item => item.TaskId == task.Id);
        if (deliveryReceiptId.HasValue)
            receiptQuery = receiptQuery.Where(item => item.Id == deliveryReceiptId.Value);

        var receipt = await receiptQuery
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.AgentWorkItemId,
                item.AgentVersion,
                item.OutcomeSummary,
                item.EvidenceJson,
                item.ToolEffectsJson,
                item.ValidationSummary,
                item.RiskSummary,
                item.EvidenceQuality,
                item.AcceptanceStatus,
                item.ReviewComment,
                item.CreatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (deliveryReceiptId.HasValue && receipt == null)
            throw new InvalidOperationException("本次运行绑定的交付凭证不存在或不属于当前任务");

        builder.AppendLine().AppendLine(deliveryReceiptId.HasValue
            ? "【指定交付凭证（系统事实，不随最新交付变化）】"
            : "【最新交付凭证（系统事实）】");
        if (receipt == null)
        {
            builder.AppendLine("暂无 Agent 正式交付凭证");
        }
        else
        {
            builder.AppendLine($"凭证：#{receipt.Id}，工作项 #{receipt.AgentWorkItemId}，Agent v{receipt.AgentVersion}，提交于 {receipt.CreatedAt:yyyy-MM-dd HH:mm}")
                .AppendLine($"验收状态：{receipt.AcceptanceStatus}；证据质量：{receipt.EvidenceQuality}")
                .AppendLine($"交付结论：{Truncate(receipt.OutcomeSummary, 2000)}")
                .AppendLine($"系统证据：{Truncate(receipt.EvidenceJson, 2000)}")
                .AppendLine($"脱敏工具影响：{Truncate(receipt.ToolEffectsJson, 1500)}")
                .AppendLine($"自动校验：{Truncate(receipt.ValidationSummary, 800)}")
                .AppendLine($"风险提示：{Truncate(receipt.RiskSummary, 800)}");
            if (!string.IsNullOrWhiteSpace(receipt.ReviewComment))
                builder.AppendLine($"人工审核意见：{Truncate(receipt.ReviewComment, 800)}");
        }

        var commitments = await _context.MeetingActionItems.AsNoTracking()
            .Where(item => item.MatchedTaskId == task.Id
                && item.IsConfirmed
                && item.MeetingMinutes != null
                && !item.MeetingMinutes.IsDeleted)
            .OrderByDescending(item => item.MeetingMinutes!.MeetingDate)
            .Take(20)
            .Select(item => new
            {
                item.Id,
                item.Title,
                item.Deadline,
                item.SupervisionStatus,
                item.EscalationLevel,
                item.SupervisionMessage,
                item.LastSupervisedAt,
                MeetingId = item.MeetingMinutesId,
                MeetingTitle = item.MeetingMinutes!.MeetingTitle,
                MeetingDate = item.MeetingMinutes.MeetingDate
            })
            .ToListAsync(cancellationToken);
        builder.AppendLine().AppendLine("【会议承诺督办（系统事实）】");
        if (commitments.Count == 0)
        {
            builder.AppendLine("当前任务没有已确认的会议行动项");
            return;
        }
        foreach (var item in commitments)
            builder.AppendLine($"- 会议 #{item.MeetingId} {item.MeetingDate:yyyy-MM-dd}「{item.MeetingTitle}」行动项 #{item.Id}「{item.Title}」｜{item.SupervisionStatus} L{item.EscalationLevel}｜截止 {item.Deadline?.ToString("yyyy-MM-dd HH:mm") ?? "未设置"}｜{Truncate(item.SupervisionMessage, 500)}｜检查 {item.LastSupervisedAt?.ToString("MM-dd HH:mm") ?? "尚未执行"}");
    }

    private async Task AppendMeetingsAsync(StringBuilder builder, int? projectId, CancellationToken cancellationToken)
    {
        if (!projectId.HasValue) throw new InvalidOperationException("会议上下文要求关联项目");
        var meetings = await _context.MeetingMinutes.AsNoTracking()
            .Where(item => item.ProjectId == projectId.Value && !item.IsDeleted && !item.IsDraft)
            .OrderByDescending(item => item.MeetingDate)
            .Take(10)
            .Select(item => new { item.Id, item.MeetingTitle, item.MeetingDate, item.AiSummary, item.MeetingContent })
            .ToListAsync(cancellationToken);
        builder.AppendLine().AppendLine("【最近会议】");
        if (meetings.Count == 0)
        {
            builder.AppendLine("暂无会议纪要");
            return;
        }
        foreach (var meeting in meetings)
            builder.AppendLine($"- #{meeting.Id} {meeting.MeetingDate:yyyy-MM-dd} {meeting.MeetingTitle}｜{Truncate(meeting.AiSummary ?? meeting.MeetingContent, 800)}");
    }

    private async Task AppendDocumentsAsync(
        StringBuilder builder,
        AgentDefinition definition,
        AiSession session,
        int userId,
        CancellationToken cancellationToken)
    {
        if (!session.ProjectId.HasValue) throw new InvalidOperationException("资料上下文要求关联项目");
        var documents = await _documentAccess.ReadDocumentsAsync(
            session.ProjectId.Value,
            definition.AgentKey,
            userId,
            session.Id,
            maxDocuments: 20,
            maxCharactersPerDocument: 3500,
            cancellationToken: cancellationToken,
            query: session.Prompt,
            totalCharacterBudget: 18000);
        builder.AppendLine().AppendLine("【已授权项目资料】");
        if (documents.Count == 0)
        {
            builder.AppendLine("暂无已授权资料");
            return;
        }
        foreach (var document in documents)
        {
            builder.AppendLine($"### #{document.Id} {document.FileName}｜{document.Category.GetDisplayName()}｜v{document.VersionNumber}")
                .AppendLine($"说明：{document.Description}")
                .AppendLine(document.ContentExcerpt);
        }
    }

    private async Task AppendReportsAsync(StringBuilder builder, int? projectId, CancellationToken cancellationToken)
    {
        if (!projectId.HasValue) throw new InvalidOperationException("报告上下文要求关联项目");
        var reports = await _context.DailyReport.AsNoTracking()
            .Where(item => item.ProjectId == projectId.Value && !item.IsDeleted)
            .OrderByDescending(item => item.ReportDate)
            .Take(10)
            .Select(item => new { item.Id, item.ReportType, item.ReportDate, item.ReportTitle, item.ReportContent })
            .ToListAsync(cancellationToken);
        builder.AppendLine().AppendLine("【最近报告】");
        if (reports.Count == 0)
        {
            builder.AppendLine("暂无报告");
            return;
        }
        foreach (var report in reports)
            builder.AppendLine($"- #{report.Id} {report.ReportDate:yyyy-MM-dd} 类型{report.ReportType} {report.ReportTitle}｜{Truncate(report.ReportContent, 800)}");
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
        if (!allowed) throw new UnauthorizedAccessException("当前用户无权访问该项目");
    }

    private async Task RecordContextReadAsync(
        AiSession session,
        IReadOnlySet<AgentContextSource> sources,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0) return;
        _context.AgentToolCalls.Add(new AgentToolCall
        {
            AiSessionId = session.Id,
            ToolName = ContextReadToolName,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ArgumentsJson = JsonSerializer.Serialize(new { sources = sources.Select(item => item.ToString()) }),
            ResultJson = JsonSerializer.Serialize(new { message = "Agent 上下文已按配置加载" }),
            RequiresApproval = false,
            RiskLevel = AgentRiskLevel.Low,
            ReviewMode = AgentToolReviewMode.Direct,
            ReviewReason = "系统按 Agent 授权范围加载只读上下文",
            Status = AgentToolCallStatus.Executed,
            CompletedAt = AppTime.Now
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "无";
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "…";
    }
}

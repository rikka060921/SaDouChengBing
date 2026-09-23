using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public sealed record AgentDeliveryEvidence(string Type, string Label, string Reference, string Source);

public sealed record AgentToolEffect(
    int ToolCallId,
    string ToolName,
    AgentToolCallStatus Status,
    AgentRiskLevel RiskLevel,
    AgentToolReviewMode ReviewMode,
    string Effect);

/// <summary>
/// 把 Agent 输出转换为可验收交付，并将人工验收与派单选择记录成可复用的表现信号。
/// 本服务只向当前 DbContext 添加或修改实体，由业务调用方统一提交事务。
/// </summary>
public sealed class AgentOutcomeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SafeResultFields =
    ["message", "taskId", "projectId", "documentId", "reportId", "meetingId", "commentId", "status"];

    private readonly ApplicationDbContext _context;
    private readonly IntegritySigningService? _signing;

    public AgentOutcomeService(ApplicationDbContext context, IntegritySigningService? signing = null)
    {
        _context = context;
        _signing = signing;
    }

    public async Task<AgentDeliveryReceipt> CreateDeliveryAsync(
        AgentWorkItem workItem,
        ToDoTask task,
        AiSession session,
        string outcomeSummary,
        CancellationToken cancellationToken = default)
    {
        var trackedExisting = _context.ChangeTracker.Entries<AgentDeliveryReceipt>()
            .Select(entry => entry.Entity)
            .FirstOrDefault(item => item.AgentWorkItemId == workItem.Id);
        if (trackedExisting != null) return trackedExisting;
        var existing = await _context.AgentDeliveryReceipts
            .FirstOrDefaultAsync(item => item.AgentWorkItemId == workItem.Id, cancellationToken);
        if (existing != null) return existing;

        var now = AppTime.Now;
        var previousPending = await _context.AgentDeliveryReceipts
            .Where(item => item.TaskId == task.Id
                && item.AcceptanceStatus == AgentDeliveryAcceptanceStatus.PendingReview)
            .ToListAsync(cancellationToken);
        foreach (var previous in previousPending)
        {
            previous.AcceptanceStatus = AgentDeliveryAcceptanceStatus.Superseded;
            previous.UpdatedAt = now;
        }

        var projectName = await _context.Project.AsNoTracking()
            .Where(item => item.Id == workItem.ProjectId)
            .Select(item => item.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? $"项目 #{workItem.ProjectId}";
        var toolCalls = await _context.AgentToolCalls.AsNoTracking()
            .Where(call => call.AiSessionId == session.Id)
            .OrderBy(call => call.Id)
            .ToListAsync(cancellationToken);

        var evidence = new List<AgentDeliveryEvidence>
        {
            new("Task", task.Title, $"task:{task.Id}", "system"),
            new("Project", projectName, $"project:{workItem.ProjectId}", "system"),
            new("AiSession", $"Session #{session.Id} · Agent v{session.AgentVersion}", $"session:{session.Id}", "system")
        };
        foreach (var source in ReadContextSources(toolCalls).Distinct(StringComparer.OrdinalIgnoreCase))
            evidence.Add(new AgentDeliveryEvidence("Context", source, $"session:{session.Id}", "context.read"));

        var effects = toolCalls
            .Where(call => !string.Equals(call.ToolName, AgentContextService.ContextReadToolName, StringComparison.OrdinalIgnoreCase))
            .Select(call => new AgentToolEffect(
                call.Id,
                call.ToolName,
                call.Status,
                call.RiskLevel,
                call.ReviewMode,
                SummarizeToolResult(call)))
            .ToList();
        var failedEffects = effects.Count(item => item.Status == AgentToolCallStatus.Failed);
        var rejectedEffects = effects.Count(item => item.Status == AgentToolCallStatus.Rejected);
        var unresolvedEffects = effects.Count(item => item.Status is AgentToolCallStatus.Proposed or AgentToolCallStatus.PendingApproval);
        var executedEffects = effects.Count(item => item.Status == AgentToolCallStatus.Executed);
        var normalizedOutcome = string.IsNullOrWhiteSpace(outcomeSummary) ? "Agent 未提供文字结论" : outcomeSummary.Trim();
        var evidenceJson = JsonSerializer.Serialize(evidence, JsonOptions);
        var effectsJson = JsonSerializer.Serialize(effects, JsonOptions);
        var evidenceQuality = evidence.Count > 3 || executedEffects > 0
            ? AgentEvidenceQuality.Strong
            : AgentEvidenceQuality.Basic;
        var validationSummary = $"系统证据 {evidence.Count} 项，已执行工具 {executedEffects} 项，失败 {failedEffects} 项，拒绝 {rejectedEffects} 项，未决 {unresolvedEffects} 项；文字结论{(string.IsNullOrWhiteSpace(outcomeSummary) ? "缺失" : "已提供")}。";
        var riskSummary = unresolvedEffects > 0
            ? $"存在 {unresolvedEffects} 项未决工具调用，不应视为已完成的业务动作。"
            : failedEffects > 0 || rejectedEffects > 0
                ? $"工具调用中失败 {failedEffects} 项、被拒绝 {rejectedEffects} 项；验收时需确认结论未将其误报为成功。"
                : "未发现未决、失败或被拒绝的工具调用。";
        var contentHash = ComputeHash(new
        {
            WorkItemId = workItem.Id,
            workItem.AgentDefinitionId,
            TaskId = task.Id,
            SessionId = session.Id,
            session.AgentVersion,
            OutcomeSummary = normalizedOutcome,
            EvidenceJson = evidenceJson,
            ToolEffectsJson = effectsJson,
            ValidationSummary = validationSummary,
            RiskSummary = riskSummary
        });

        var receipt = new AgentDeliveryReceipt
        {
            AgentWorkItemId = workItem.Id,
            AgentDefinitionId = workItem.AgentDefinitionId,
            ProjectId = workItem.ProjectId,
            TaskId = task.Id,
            AiSessionId = session.Id,
            AgentVersion = session.AgentVersion,
            OutcomeSummary = normalizedOutcome,
            EvidenceJson = evidenceJson,
            ToolEffectsJson = effectsJson,
            ValidationSummary = validationSummary,
            RiskSummary = riskSummary,
            ContentHash = contentHash,
            EvidenceQuality = evidenceQuality,
            AcceptanceStatus = AgentDeliveryAcceptanceStatus.PendingReview,
            CreatedAt = now,
            UpdatedAt = now
        };
        if (_signing?.IsConfigured == true)
        {
            receipt.ContentSignature = _signing.SignHash(contentHash);
            receipt.SignatureKeyId = _signing.CurrentKeyId;
        }
        else
        {
            receipt.SignatureKeyId = "unsigned-development";
        }
        _context.AgentDeliveryReceipts.Add(receipt);
        workItem.DeliveryReceipt = receipt;
        await AddSignalAsync(
            workItem.AgentDefinitionId,
            workItem.ProjectId,
            task.Id,
            workItem.Id,
            receipt,
            null,
            AgentPerformanceEventType.WorkSubmitted,
            2,
            $"work-submitted:{workItem.Id}",
            "Agent 已提交交付，等待验收检查",
            cancellationToken);
        return receipt;
    }

    public async Task ApplyAutomaticReviewAsync(AgentDeliveryReceipt receipt, AutomaticAcceptanceResult result,
        CancellationToken ct = default)
    {
        if (receipt.AcceptanceStatus != AgentDeliveryAcceptanceStatus.PendingReview) return;
        receipt.ReviewComment = Truncate(result.Reason, 2000);
        if (!result.Accepted) return;
        receipt.AcceptanceStatus = AgentDeliveryAcceptanceStatus.AutomaticallyAccepted;
        receipt.ReviewedByUserId = null;
        receipt.ReviewedAt = receipt.UpdatedAt = AppTime.Now;
        await AddSignalAsync(receipt.AgentDefinitionId, receipt.ProjectId, receipt.TaskId, receipt.AgentWorkItemId,
            receipt, null, AgentPerformanceEventType.AutomaticallyAccepted, 0,
            $"delivery-auto-review:{receipt.AgentWorkItemId}", result.Reason, ct);
    }

    public async Task<AgentDeliveryReceipt?> ApplyHumanReviewAsync(
        int taskId,
        bool approved,
        int reviewerId,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        var receipt = await _context.AgentDeliveryReceipts
            .Where(item => item.TaskId == taskId
                && item.AcceptanceStatus == AgentDeliveryAcceptanceStatus.PendingReview)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (receipt == null) return null;

        var now = AppTime.Now;
        receipt.AcceptanceStatus = approved
            ? AgentDeliveryAcceptanceStatus.Accepted
            : AgentDeliveryAcceptanceStatus.Rejected;
        receipt.ReviewedByUserId = reviewerId;
        receipt.ReviewComment = Truncate(comment?.Trim() ?? string.Empty, 2000);
        receipt.ReviewedAt = now;
        receipt.UpdatedAt = now;
        await AddSignalAsync(
            receipt.AgentDefinitionId,
            receipt.ProjectId,
            receipt.TaskId,
            receipt.AgentWorkItemId,
            receipt,
            null,
            approved ? AgentPerformanceEventType.HumanAccepted : AgentPerformanceEventType.HumanRejected,
            approved ? 8 : -10,
            $"delivery-review:{receipt.Id}",
            approved ? "人工验收通过" : $"人工验收驳回：{Truncate(comment?.Trim() ?? "未填写原因", 800)}",
            cancellationToken);
        return receipt;
    }

    public Task RecordFailureAsync(
        AgentWorkItem workItem,
        string reason,
        CancellationToken cancellationToken = default)
        => AddSignalAsync(
            workItem.AgentDefinitionId,
            workItem.ProjectId,
            workItem.TaskId,
            workItem.Id,
            null,
            null,
            AgentPerformanceEventType.WorkFailed,
            -5,
            $"work-failed:{workItem.Id}",
            Truncate(reason, 1000),
            cancellationToken);

    public async Task RecordDispatchSelectionAsync(
        AgentDispatchDecision decision,
        int selectedAgentDefinitionId,
        CancellationToken cancellationToken = default)
    {
        if (!decision.RecommendedAgentDefinitionId.HasValue)
        {
            await AddSignalAsync(selectedAgentDefinitionId, decision.ProjectId, decision.TaskId,
                null, null, decision, AgentPerformanceEventType.HumanSelected, 0,
                $"dispatch-selected:{decision.Id}:{selectedAgentDefinitionId}",
                "人工从职责匹配候选中选择执行者；该选择不计作质量评分", cancellationToken);
            return;
        }
        if (decision.RecommendedAgentDefinitionId.Value == selectedAgentDefinitionId)
        {
            await AddSignalAsync(
                selectedAgentDefinitionId,
                decision.ProjectId,
                decision.TaskId,
                null,
                null,
                decision,
                AgentPerformanceEventType.DispatchRecommendationConfirmed,
                3,
                $"dispatch-confirmed:{decision.Id}:{selectedAgentDefinitionId}",
                "人工确认了调度器推荐的 Agent",
                cancellationToken);
            return;
        }

        await AddSignalAsync(
            decision.RecommendedAgentDefinitionId.Value,
            decision.ProjectId,
            decision.TaskId,
            null,
            null,
            decision,
            AgentPerformanceEventType.DispatchRecommendationOverridden,
            -4,
            $"dispatch-overridden:{decision.Id}:{decision.RecommendedAgentDefinitionId.Value}",
            $"人工未采用推荐，改选 Agent #{selectedAgentDefinitionId}",
            cancellationToken);
        await AddSignalAsync(
            selectedAgentDefinitionId,
            decision.ProjectId,
            decision.TaskId,
            null,
            null,
            decision,
            AgentPerformanceEventType.HumanSelected,
            5,
            $"dispatch-selected:{decision.Id}:{selectedAgentDefinitionId}",
            $"人工选择该 Agent，替代推荐 Agent #{decision.RecommendedAgentDefinitionId.Value}",
            cancellationToken);
    }

    public static IReadOnlyList<AgentDeliveryEvidence> ParseEvidence(string? json)
        => DeserializeList<AgentDeliveryEvidence>(json);

    public static IReadOnlyList<AgentToolEffect> ParseToolEffects(string? json)
        => DeserializeList<AgentToolEffect>(json);

    public static bool VerifyContentHash(AgentDeliveryReceipt receipt)
    {
        var expected = ComputeHash(new
        {
            WorkItemId = receipt.AgentWorkItemId,
            receipt.AgentDefinitionId,
            TaskId = receipt.TaskId,
            SessionId = receipt.AiSessionId,
            receipt.AgentVersion,
            receipt.OutcomeSummary,
            receipt.EvidenceJson,
            receipt.ToolEffectsJson,
            receipt.ValidationSummary,
            receipt.RiskSummary
        });
        return string.Equals(expected, receipt.ContentHash, StringComparison.OrdinalIgnoreCase);
    }

    public bool VerifyAuthenticity(AgentDeliveryReceipt receipt)
    {
        if (!VerifyContentHash(receipt)) return false;
        if (string.IsNullOrWhiteSpace(receipt.ContentSignature)) return false;
        return _signing?.VerifyHash(receipt.ContentHash, receipt.ContentSignature, receipt.SignatureKeyId) == true;
    }

    private async Task AddSignalAsync(
        int agentDefinitionId,
        int? projectId,
        int? taskId,
        int? workItemId,
        AgentDeliveryReceipt? receipt,
        AgentDispatchDecision? dispatchDecision,
        AgentPerformanceEventType eventType,
        double scoreDelta,
        string eventKey,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_context.ChangeTracker.Entries<AgentPerformanceSignal>()
            .Any(entry => entry.Entity.EventKey == eventKey)) return;
        if (await _context.AgentPerformanceSignals.AsNoTracking()
            .AnyAsync(item => item.EventKey == eventKey, cancellationToken)) return;

        _context.AgentPerformanceSignals.Add(new AgentPerformanceSignal
        {
            AgentDefinitionId = agentDefinitionId,
            ProjectId = projectId,
            TaskId = taskId,
            AgentWorkItemId = workItemId,
            DeliveryReceipt = receipt,
            DispatchDecision = dispatchDecision,
            EventType = eventType,
            ScoreDelta = scoreDelta,
            EventKey = eventKey,
            Reason = Truncate(reason, 1000),
            CreatedAt = AppTime.Now
        });
    }

    private static IReadOnlyList<string> ReadContextSources(IEnumerable<AgentToolCall> calls)
    {
        var result = new List<string>();
        foreach (var call in calls.Where(call => string.Equals(
                     call.ToolName,
                     AgentContextService.ContextReadToolName,
                     StringComparison.OrdinalIgnoreCase)))
        {
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(call.ArgumentsJson);
                if (!document.RootElement.TryGetProperty("sources", out var sources)
                    || sources.ValueKind != JsonValueKind.Array) continue;
                foreach (var source in sources.EnumerateArray())
                {
                    var value = source.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
                }
            }
            catch (JsonException)
            {
                // 损坏的审计参数不阻断交付生成，且不会把原始内容复制到证据中。
            }
            finally
            {
                document?.Dispose();
            }
        }
        return result;
    }

    private static string SummarizeToolResult(AgentToolCall call)
    {
        if (call.Status != AgentToolCallStatus.Executed)
            return call.Status switch
            {
                AgentToolCallStatus.PendingApproval => "等待人工审批，尚未执行",
                AgentToolCallStatus.Rejected => "人工已拒绝，未执行",
                AgentToolCallStatus.Failed => "执行失败",
                _ => "仅提出调用，尚未执行"
            };

        try
        {
            using var document = JsonDocument.Parse(call.ResultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return "已执行";
            var safe = new Dictionary<string, string>();
            foreach (var field in SafeResultFields)
            {
                if (!document.RootElement.TryGetProperty(field, out var value)) continue;
                safe[field] = Truncate(value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString(), 240);
            }
            return safe.Count == 0
                ? "已执行（详细返回保留在原始工具审计记录中）"
                : string.Join("；", safe.Select(item => $"{item.Key}={item.Value}"));
        }
        catch (JsonException)
        {
            return "已执行（返回格式无法解析，请查看原始工具审计记录）";
        }
    }

    private static string ComputeHash<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static IReadOnlyList<T> DeserializeList<T>(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json ?? "[]", JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Agents.WorkItems;

[Authorize]
public sealed class DetailsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AgentRunQueueService _runQueue;
    private readonly AgentOutcomeService _outcomes;

    public DetailsModel(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        AgentRunQueueService runQueue,
        AgentOutcomeService outcomes)
    {
        _context = context;
        _userManager = userManager;
        _runQueue = runQueue;
        _outcomes = outcomes;
    }

    public AgentWorkItem WorkItem { get; private set; } = default!;
    public AgentDeliveryReceipt? Receipt { get; private set; }
    public IReadOnlyList<AgentDeliveryEvidence> Evidence { get; private set; } = [];
    public IReadOnlyList<AgentToolEffect> ToolEffects { get; private set; } = [];
    public List<AgentPerformanceSignal> PerformanceSignals { get; private set; } = [];
    public List<AgentAcceptanceRecommendation> AcceptanceRecommendations { get; private set; } = [];
    public bool ReceiptHashValid { get; private set; }
    public bool ReceiptSignatureValid { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();

        var workItem = await _context.AgentWorkItems.AsNoTracking()
            .Include(item => item.AgentDefinition)
            .Include(item => item.Project)
            .Include(item => item.Task)
            .Include(item => item.RequestedByUser)
            .Include(item => item.AiSession)
            .FirstOrDefaultAsync(item => item.Id == id);
        if (workItem == null) return NotFound();

        if (!await CanAccessAsync(workItem, user)) return Forbid();

        WorkItem = workItem;
        Receipt = await _context.AgentDeliveryReceipts.AsNoTracking()
            .Include(item => item.ReviewedByUser)
            .FirstOrDefaultAsync(item => item.AgentWorkItemId == id);
        if (Receipt != null)
        {
            Evidence = AgentOutcomeService.ParseEvidence(Receipt.EvidenceJson);
            ToolEffects = AgentOutcomeService.ParseToolEffects(Receipt.ToolEffectsJson);
            ReceiptHashValid = AgentOutcomeService.VerifyContentHash(Receipt);
            ReceiptSignatureValid = _outcomes.VerifyAuthenticity(Receipt);
            PerformanceSignals = await _context.AgentPerformanceSignals.AsNoTracking()
                .Where(item => item.AgentWorkItemId == id || item.DeliveryReceiptId == Receipt.Id)
                .OrderBy(item => item.CreatedAt)
                .ToListAsync();
            AcceptanceRecommendations = await _context.AgentAcceptanceRecommendations.AsNoTracking()
                .Where(item => item.DeliveryReceiptId == Receipt.Id)
                .OrderByDescending(item => item.CreatedAt)
                .ToListAsync();
        }
        return Page();
    }

    public async Task<IActionResult> OnPostRunAcceptanceAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        var workItem = await _context.AgentWorkItems.AsNoTracking()
            .Include(item => item.Project)
            .Include(item => item.Task)
            .FirstOrDefaultAsync(item => item.Id == id);
        if (workItem == null) return NotFound();
        if (!await CanAccessAsync(workItem, user)) return Forbid();
        var receipt = await _context.AgentDeliveryReceipts.AsNoTracking()
            .FirstOrDefaultAsync(item => item.AgentWorkItemId == id);
        if (receipt == null)
        {
            TempData["ErrorMessage"] = "当前工作项还没有可供审查的交付凭证";
            return RedirectToPage(new { id });
        }

        try
        {
            var prompt = $"请独立审查任务 #{workItem.TaskId}「{workItem.Task?.Title}」的指定交付凭证 #{receipt.Id}。只给建议，不执行正式验收。最终必须输出且只输出一个 <acceptance-result> 标签，标签内为 JSON：{{\"verdict\":\"accept 或 reject 或 human_review\",\"confidence\":0到1,\"summary\":\"结论\",\"evidenceCoverage\":[\"已覆盖证据\"],\"missingItems\":[\"缺失项\"],\"risks\":[\"风险\"],\"humanReviewQuestions\":[\"需要人工复核的问题\"]}}。";
            var job = await _runQueue.EnqueueNewAsync(
                "delivery-acceptance",
                prompt,
                user,
                workItem.ProjectId,
                workItem.TaskId,
                cancellationToken: HttpContext.RequestAborted,
                deliveryReceiptId: receipt.Id);
            return RedirectToPage("/AiSessions/Details", new { id = job.AiSessionId });
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            TempData["ErrorMessage"] = ex.Message;
            return RedirectToPage(new { id });
        }
    }

    private Task<bool> CanAccessAsync(AgentWorkItem workItem, ApplicationUser user)
    {
        if (user.Role == UserRole.systemAdmin || workItem.Project?.LeaderUserId == user.Id)
            return Task.FromResult(true);
        return _context.ProjectUsers.AsNoTracking()
            .AnyAsync(item => item.ProjectId == workItem.ProjectId && item.UserId == user.Id);
    }

    public static string GetAcceptanceLabel(AgentDeliveryAcceptanceStatus status) => status switch
    {
        AgentDeliveryAcceptanceStatus.Accepted => "验收通过",
        AgentDeliveryAcceptanceStatus.AutomaticallyAccepted => "自动验收通过",
        AgentDeliveryAcceptanceStatus.Rejected => "验收驳回",
        AgentDeliveryAcceptanceStatus.Superseded => "已被新交付替代",
        _ => "待人工验收"
    };

    public static string GetSignalLabel(AgentPerformanceEventType eventType) => eventType switch
    {
        AgentPerformanceEventType.WorkSubmitted => "提交交付",
        AgentPerformanceEventType.WorkFailed => "执行失败",
        AgentPerformanceEventType.HumanAccepted => "人工验收通过",
        AgentPerformanceEventType.AutomaticallyAccepted => "系统自动验收通过",
        AgentPerformanceEventType.HumanRejected => "人工验收驳回",
        AgentPerformanceEventType.DispatchRecommendationConfirmed => "调度推荐被采纳",
        AgentPerformanceEventType.DispatchRecommendationOverridden => "调度推荐被改选",
        AgentPerformanceEventType.HumanSelected => "人工选中",
        _ => eventType.ToString()
    };

    public static string GetRecommendationLabel(AgentAcceptanceVerdict verdict) => verdict switch
    {
        AgentAcceptanceVerdict.RecommendAccept => "建议通过",
        AgentAcceptanceVerdict.RecommendReject => "建议驳回",
        _ => "需要人工判断"
    };

    public static IReadOnlyList<string> ParseRecommendationItems(string? json)
        => AgentAcceptanceService.ParseItems(json);
}

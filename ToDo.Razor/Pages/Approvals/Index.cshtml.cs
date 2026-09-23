using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Approvals;

[Authorize]
public class IndexModel : PageModel
{
    private readonly ApprovalRequestService _service;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(ApprovalRequestService service, UserManager<ApplicationUser> userManager, ILogger<IndexModel> logger)
    {
        _service = service;
        _userManager = userManager;
        _logger = logger;
    }

    public List<ApprovalRequest> Items { get; set; } = new();
    public HashSet<int> ApprovableProjectIds { get; set; } = new();
    public HashSet<int> ApprovableRequestIds { get; set; } = new();
    [BindProperty(SupportsGet = true)] public int? RequestId { get; set; }
    public bool IsAuthentic(ApprovalRequest request) => _service.VerifyPayloadAuthenticity(request);

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");
        Items = await _service.GetAccessibleAsync(user, requestId: RequestId);
        foreach (var projectId in Items.Select(item => item.ProjectId).Distinct())
        {
            if (await _service.CanApproveAsync(projectId, user)) ApprovableProjectIds.Add(projectId);
        }
        foreach (var item in Items.Where(item => item.Status == ApprovalRequestStatus.Pending))
        {
            if (await _service.CanApproveRequestAsync(item, user)) ApprovableRequestIds.Add(item.Id);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(int id, string? reviewComment)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();
        try
        {
            await _service.ApproveAsync(id, user, reviewComment);
            TempData["SuccessMessage"] = "审批通过，敏感操作已执行";
        }
        catch (UnauthorizedAccessException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to approve request {ApprovalId}", id);
            TempData["ErrorMessage"] = "审批处理失败，请稍后重试";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRejectAsync(int id, string? reviewComment)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();
        try
        {
            await _service.RejectAsync(id, user, reviewComment);
            TempData["SuccessMessage"] = "审批请求已驳回，未执行任何写入";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reject request {ApprovalId}", id);
            TempData["ErrorMessage"] = "驳回处理失败，请稍后重试";
        }
        return RedirectToPage();
    }
}

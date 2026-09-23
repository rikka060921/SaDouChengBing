using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.RedBlue;

[Authorize]
public class DetailsModel : PageModel
{
    private readonly RedBlueService _service;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApprovalRequestService _approvals;
    private readonly ILogger<DetailsModel> _logger;
    public DetailsModel(RedBlueService service, UserManager<ApplicationUser> userManager, ApprovalRequestService approvals, ILogger<DetailsModel> logger)
    {
        _service = service;
        _userManager = userManager;
        _approvals = approvals;
        _logger = logger;
    }
    public RedBlueSession? Session { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Session = await _service.GetDetailsAsync(id);
        if (Session == null) return NotFound();
        var user = await _userManager.GetUserAsync(User);
        return user == null || !await _service.CanAccessSessionAsync(Session, user) ? Forbid() : Page();
    }

    public async Task<IActionResult> OnPostRunAsync(int id)
    {
        Session = await _service.GetDetailsAsync(id);
        if (Session == null) return NotFound();
        var user = await _userManager.GetUserAsync(User);
        if (user == null || !await _service.CanAccessSessionAsync(Session, user)) return Forbid();
        try
        {
            await _service.RunAsync(Session);
            TempData["SuccessMessage"] = "红蓝对抗已完成";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run red-blue session {SessionId}", id);
            TempData["ErrorMessage"] = "红蓝对抗执行失败，请稍后重试";
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRequestWritebackAsync(int id)
    {
        Session = await _service.GetDetailsAsync(id);
        if (Session == null) return NotFound();
        var user = await _userManager.GetUserAsync(User);
        if (user == null || !await _service.CanAccessSessionAsync(Session, user)) return Forbid();
        if (!Session.ProjectId.HasValue || Session.Status != RedBlueSessionStatus.Completed || string.IsNullOrWhiteSpace(Session.FinalDecision))
        {
            TempData["ErrorMessage"] = "对抗尚未完成或未关联项目，无法申请写回";
            return RedirectToPage(new { id });
        }

        var content = $"# {Session.Topic}\n\n## 决策目标\n\n{Session.Objective}\n\n## 最终结论\n\n{Session.FinalDecision}\n\n## 对抗结果\n\n胜方：{Session.Winner}\n轮数：{Session.CurrentRound}\n";
        var payload = JsonSerializer.Serialize(new GeneratedDocumentPayload
        {
            FileName = $"红蓝对抗结论-{Session.Id}.md",
            Content = content,
            Description = $"由红蓝对抗 #{Session.Id} 生成，人工审批后写入"
        });
        await _approvals.RequestAsync(
            Session.ProjectId.Value,
            user.Id,
            "RedBlueSession",
            Session.Id,
            ApprovalRequestService.RedBlueDecisionDocument,
            $"将红蓝对抗「{Session.Topic}」的最终结论写入项目资料",
            payload);
        TempData["SuccessMessage"] = "写回申请已提交，审批通过前不会修改项目资料";
        return RedirectToPage(new { id });
    }
}

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.AiSessions;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class DetailsModel : PageModel
{
    private readonly AiSessionService _sessions;
    private readonly AgentRunQueueService _runQueue;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<DetailsModel> _logger;

    public DetailsModel(
        AiSessionService sessions,
        AgentRunQueueService runQueue,
        UserManager<ApplicationUser> userManager,
        ILogger<DetailsModel> logger)
    {
        _sessions = sessions;
        _runQueue = runQueue;
        _userManager = userManager;
        _logger = logger;
    }

    public AiSession Item { get; set; } = null!;
    public AgentRunJob? CurrentRunJob { get; set; }

    [BindProperty]
    [StringLength(8000, ErrorMessage = "单次指令不能超过 8000 个字符")]
    public string ContinuePrompt { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        return await LoadAsync(id) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostContinueAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");
        Item = await _sessions.GetDetailsAsync(id, user) ?? null!;
        if (Item == null) return NotFound();
        if (string.IsNullOrWhiteSpace(ContinuePrompt))
        {
            ModelState.AddModelError(nameof(ContinuePrompt), "请输入要继续发送的内容");
            return Page();
        }

        try
        {
            await _runQueue.EnqueueContinuationAsync(id, ContinuePrompt.Trim(), user, HttpContext.RequestAborted);
            return RedirectToPage(new { id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to continue AI session {SessionId}", id);
            ModelState.AddModelError(string.Empty, "继续执行失败，请稍后重试");
            Item = await _sessions.GetDetailsAsync(id, user) ?? Item;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostCancelRunAsync(int id, long jobId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");
        try
        {
            await _runQueue.CancelAsync(jobId, user, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = "后台运行任务已取消";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel AI run job {JobId} in session {SessionId}", jobId, id);
            TempData["ErrorMessage"] = "取消运行任务失败，请稍后重试";
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCloseAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");
        var item = await _sessions.GetDetailsAsync(id, user);
        if (item == null) return NotFound();
        if (item.Status == AiSessionStatus.Running)
        {
            TempData["ErrorMessage"] = "Session 正在执行，暂时不能结束";
            return RedirectToPage(new { id });
        }
        await _sessions.CloseAsync(item);
        TempData["SuccessMessage"] = "Session 已结束";
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return false;
        Item = await _sessions.GetDetailsAsync(id, user) ?? null!;
        if (Item != null) CurrentRunJob = await _runQueue.GetCurrentAsync(id, HttpContext.RequestAborted);
        return Item != null;
    }
}

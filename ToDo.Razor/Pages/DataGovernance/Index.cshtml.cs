using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.DataGovernance;

[Authorize(Roles = "systemAdmin")]
public sealed class IndexModel : PageModel
{
    private readonly DataRetentionService _retention;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        DataRetentionService retention,
        UserManager<ApplicationUser> userManager,
        ILogger<IndexModel> logger)
    {
        _retention = retention;
        _userManager = userManager;
        _logger = logger;
    }

    public DataRetentionPreview Preview { get; private set; } = null!;
    public List<DataRetentionRun> RecentRuns { get; private set; } = [];
    [BindProperty] public bool ConfirmExecution { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostExecuteAsync()
    {
        if (!ConfirmExecution)
        {
            TempData["ErrorMessage"] = "请先确认已理解归档和清理范围";
            return RedirectToPage();
        }

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        try
        {
            var result = await _retention.RunAsync(user.Id, "manual", cancellationToken: HttpContext.RequestAborted);
            TempData[result.Executed ? "SuccessMessage" : "ErrorMessage"] = result.Executed
                ? $"执行完成：归档 {result.ArchivedSessionCount} 个 Session，清理 {result.DeletedNotificationCount} 条已读通知"
                : result.Message;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "手动数据留存任务执行失败");
            TempData["ErrorMessage"] = "执行失败，系统已记录本次异常，请查看最近执行记录和服务日志";
        }
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Preview = await _retention.PreviewAsync(cancellationToken: HttpContext.RequestAborted);
        RecentRuns = await _retention.GetRecentRunsAsync(cancellationToken: HttpContext.RequestAborted);
    }
}

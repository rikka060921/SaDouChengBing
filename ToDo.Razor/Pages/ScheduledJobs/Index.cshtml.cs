using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.ScheduledJobs;

[Authorize(Roles = "systemAdmin")]
public class IndexModel : PageModel
{
    private readonly ScheduledJobService _service;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly PersonalDailySummaryService _personalSummaries;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        ScheduledJobService service,
        UserManager<ApplicationUser> userManager,
        PersonalDailySummaryService personalSummaries,
        ILogger<IndexModel> logger)
    {
        _service = service;
        _userManager = userManager;
        _personalSummaries = personalSummaries;
        _logger = logger;
    }

    public List<ScheduledJob> Jobs { get; set; } = new();
    public List<ProjectSummaryCheckpoint> SummaryCheckpoints { get; set; } = new();
    public List<ToDo.Entities.DailySummary.UserSummaryCheckpoint> UserSummaryCheckpoints { get; set; } = new();
    public List<TeamReportCheckpoint> TeamReportCheckpoints { get; set; } = new();

    [BindProperty]
    public string Name { get; set; } = string.Empty;

    [BindProperty]
    public string RunAtText { get; set; } = "18:00";

    [BindProperty]
    public ScheduledJobType JobType { get; set; } = ScheduledJobType.DailyReport;

    public async Task OnGetAsync()
    {
        Jobs = await _service.GetAllAsync();
        SummaryCheckpoints = await _service.GetSummaryCheckpointsAsync();
        UserSummaryCheckpoints = await _personalSummaries.GetCheckpointsAsync();
        TeamReportCheckpoints = await _service.GetTeamReportCheckpointsAsync();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (!TimeSpan.TryParseExact(RunAtText, new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out var runAt))
        {
            ModelState.AddModelError(nameof(RunAtText), "运行时间格式应为 HH:mm");
            await OnGetAsync();
            return Page();
        }

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");

        if (JobType == ScheduledJobType.PersonalDailySummary)
            await _service.CreatePersonalDailySummaryJobAsync(string.IsNullOrWhiteSpace(Name) ? "每个人的每日报告" : Name, runAt, user.Id);
        else if (JobType == ScheduledJobType.TeamDailySummary)
        {
            ModelState.AddModelError(nameof(JobType), "团队汇报会在个人日报执行后自动汇总，无需单独创建定时任务");
            await OnGetAsync();
            return Page();
        }
        else
            await _service.CreateDailyReportJobAsync(string.IsNullOrWhiteSpace(Name) ? "每日自动日报" : Name, runAt, user.Id);
        TempData["SuccessMessage"] = "定时任务已创建";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        await _service.ToggleAsync(id);
        TempData["SuccessMessage"] = "定时任务状态已更新";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRunNowAsync(int id)
    {
        try
        {
            await _service.RunNowAsync(id);
            TempData["SuccessMessage"] = "任务已执行";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "立即执行定时任务失败，JobId={JobId}", id);
            TempData["ErrorMessage"] = "执行定时任务失败，请稍后重试";
        }

        return RedirectToPage();
    }
}

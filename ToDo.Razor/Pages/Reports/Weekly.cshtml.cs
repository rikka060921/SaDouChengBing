using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Reports;

[Authorize]
public sealed class WeeklyModel(ApplicationDbContext context, WorkReportService reports) : PageModel
{
    [BindProperty(SupportsGet = true)] public int ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime StartDate { get; set; } = AppTime.Today.AddDays(-((int)AppTime.Today.DayOfWeek + 6) % 7);
    [BindProperty(SupportsGet = true)] public DateTime EndDate { get; set; } = AppTime.Today;
    [BindProperty] public bool Regenerate { get; set; }
    public List<SelectListItem> Projects { get; set; } = [];
    public List<DailyReport> RecentReports { get; set; } = [];
    private int ActorId => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
    public async Task<IActionResult> OnGetAsync() { if (ActorId == 0) return Challenge(); await LoadAsync(); return Page(); }
    public async Task<IActionResult> OnPostAsync()
    {
        if (ActorId == 0) return Challenge();
        try
        {
            if (ModelState.IsValid)
            {
                var report = await reports.GenerateWeeklyAsync(ActorId, ProjectId, StartDate, EndDate, Regenerate, HttpContext.RequestAborted);
                return RedirectToPage("./Details", new { id = report.Id });
            }
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HttpContext.RequestServices.GetService<ILogger<WeeklyModel>>()?.LogError(ex, "Weekly report generation failed for {ProjectId}", ProjectId);
            ModelState.AddModelError(string.Empty, "周报未生成成功，请稍后重试；已有报告未被覆盖。");
        }
        await LoadAsync(); return Page();
    }
    private async Task LoadAsync()
    {
        Response.Headers.CacheControl = "private, no-store";
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == ActorId && !u.IsDeleted && u.Status == UserStatus.Active);
        if (user == null) return;
        Projects = await MemberReportAccess.Projects(context, user, true).OrderBy(p => p.Name)
            .Select(p => new SelectListItem(p.Name, p.Id.ToString())).ToListAsync();
        ViewData["CanViewMemberReports"] = await MemberReportAccess.Projects(context, user).AnyAsync();
        var visible = MemberReportAccess.Projects(context, user).Select(p => p.Id);
        RecentReports = await context.DailyReport.AsNoTracking().Include(r => r.Project).Where(r => !r.IsDeleted
            && r.ReportType == 2 && r.SourceSnapshotJson != null && visible.Contains(r.ProjectId))
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(30).ToListAsync();
    }
}

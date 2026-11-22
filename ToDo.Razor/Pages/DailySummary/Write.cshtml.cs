using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;

namespace ToDo.Razor.Pages.DailySummary;

[Authorize]
public sealed class WriteModel(ApplicationDbContext context, WorkReportService reports) : PageModel
{
    [BindProperty(SupportsGet = true)] public int ProjectId { get; set; }
    [BindProperty(SupportsGet = true), DataType(DataType.Date)] public DateTime ReportDate { get; set; } = ToDo.Entities.AppTime.Today;
    [BindProperty, Required(ErrorMessage = "请输入今天的工作或对系统记录的校订"), StringLength(12000)] public string ManualContent { get; set; } = string.Empty;
    [BindProperty] public int ExpectedRevision { get; set; }
    public List<SelectListItem> Projects { get; set; } = [];
    public List<ToDo.Entities.DailyReportSupplement> History { get; set; } = [];
    private int ActorId => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
    public async Task<IActionResult> OnGetAsync()
    {
        if (ActorId == 0) return Challenge();
        await LoadAsync();
        if (ProjectId > 0)
        {
            if (!Projects.Any(p => p.Value == ProjectId.ToString())) return Forbid();
            var latest = History.FirstOrDefault();
            ManualContent = latest?.Content ?? string.Empty; ExpectedRevision = latest?.Revision ?? 0;
        }
        return Page();
    }
    public async Task<IActionResult> OnPostAsync()
    {
        if (ActorId == 0) return Challenge();
        try
        {
            if (ModelState.IsValid)
            {
                await reports.SavePersonalNoteAsync(ActorId, ProjectId, ReportDate, ManualContent, ExpectedRevision, HttpContext.RequestAborted);
                TempData["Message"] = "人工日报已保存；系统生成内容和以往修订均保留。";
                return RedirectToPage("./Details", new { selectDate = ReportDate.ToString("yyyy-MM-dd") });
            }
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HttpContext.RequestServices.GetService<ILogger<WriteModel>>()?.LogError(ex, "Saving personal daily note failed");
            ModelState.AddModelError(string.Empty, "保存失败，输入已保留，请复制备份后重试。");
            // A failed database must not be queried again before returning the submitted text.
            return Page();
        }
        await LoadAsync(); return Page();
    }
    private async Task LoadAsync()
    {
        Response.Headers.CacheControl = "private, no-store";
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == ActorId
            && !u.IsDeleted && u.Status == ToDo.Entities.UserStatus.Active);
        if (user == null) return;
        Projects = (await new DailyReportService(context, null!).GetAccessibleProjects(user, true))
            .Select(p => new SelectListItem(p.Text, p.Value)).ToList();
        // History query is scoped to the authenticated owner, never a submitted UserId.
        var allowedIds = Projects.Select(p => int.Parse(p.Value)).ToArray();
        History = await context.DailyReportSupplements.AsNoTracking().Where(n => n.UserId == ActorId && n.ProjectId == ProjectId
            && n.ReportDate == ReportDate.Date && allowedIds.Contains(n.ProjectId))
            .OrderByDescending(n => n.Revision).Take(30).ToListAsync();
    }
}

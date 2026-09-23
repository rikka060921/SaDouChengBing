using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Reports;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IDailyReportService _service;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly PersonalDailySummaryService _personalSummaries;
    private readonly ToDo.Context.ApplicationDbContext _context;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        IDailyReportService service,
        UserManager<ApplicationUser> userManager,
        PersonalDailySummaryService personalSummaries,
        ToDo.Context.ApplicationDbContext context,
        ILogger<IndexModel> logger)
    {
        _service = service;
        _userManager = userManager;
        _personalSummaries = personalSummaries;
        _context = context;
        _logger = logger;
    }

    public List<DailyReport> DailyReports { get; set; } = new();
    public Dictionary<int, bool> CanDeleteDictionary { get; set; } = new();
    public List<ApplicationUser> AllUsers { get; set; } = new();
    /// <summary>当前用户是否担任任一项目的负责人（用于显示「生成团队汇报」按钮）</summary>
    public bool IsAnyProjectLeader { get; set; }
    /// <summary>用户可生成团队汇报的项目列表（仅其作为负责人的未删除活跃项目）</summary>
    public List<SelectListItem> LeaderProjectOptions { get; set; } = new();

    [BindProperty(SupportsGet = true)] public string? Tab { get; set; } = "project";
    [BindProperty(SupportsGet = true)] public string? FilterKeyword { get; set; }
    [BindProperty(SupportsGet = true)] public int? FilterProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public int? FilterReportType { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterReporter { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? FilterStartDate { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? FilterEndDate { get; set; }
    [BindProperty(SupportsGet = true)] public int CurrentPage { get; set; } = 1;

    public int PageSize { get; set; } = 10;
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public int CurrentUserId { get; set; }
    public List<SelectListItem> ProjectOptions { get; set; } = new();
    public List<SelectListItem> ReportTypeOptions { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
            return RedirectToPage("/Account/Login", new { returnUrl = Request.Path + Request.QueryString });

        // 规范化 tab：不是 team 就按 project 处理
        if (!string.Equals(Tab, "team", StringComparison.OrdinalIgnoreCase))
            Tab = "project";

        // 团队日报 tab：强制报告类型=团队汇报，且只在有权限时才显示
        if (string.Equals(Tab, "team", StringComparison.OrdinalIgnoreCase))
        {
            FilterReportType = 4;
        }
        else
        {
            // 项目日报 tab：固定为「日报」类型，不再提供周报/月报/团队汇报下拉选项
            FilterReportType = 1;
        }

        CurrentUserId = currentUser.Id;
        await LoadFilterOptionsAsync(currentUser);
        await LoadLeaderProjectsAsync(currentUser);

        var (items, totalCount) = await _service.GetFilteredDailyReports(
            FilterProjectId,
            FilterReportType,
            FilterReporter ?? string.Empty,
            FilterKeyword ?? string.Empty,
            FilterStartDate,
            FilterEndDate,
            CurrentPage,
            PageSize,
            currentUser,
            // 项目日报 Tab：排除团队汇报类型（ReportType=4）
            excludeTeamReport: string.Equals(Tab, "project", StringComparison.OrdinalIgnoreCase));

        DailyReports = items;
        TotalCount = totalCount;
        TotalPages = TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);
        CurrentPage = Math.Clamp(CurrentPage, 1, TotalPages);

        var reporterIds = DailyReports.Select(item => item.ReporterId).Distinct().ToList();
        AllUsers = await _userManager.Users
            .Where(user => reporterIds.Contains(user.Id))
            .ToListAsync();

        CanDeleteDictionary = await _service.GetCanDeletePermissions(DailyReports, currentUser);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int? id)
    {
        if (!id.HasValue) return NotFound();

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
            return RedirectToPage("/Account/Login");

        var (success, message) = await _service.DeleteDailyReport(id.Value, currentUser);
        TempData[success ? "SuccessMessage" : "ErrorMessage"] = message;
        return RedirectToPage("./Index", new { tab = Tab });
    }

    /// <summary>项目负责人手动触发单个项目的团队汇报</summary>
    public async Task<IActionResult> OnPostGenerateTeamReportAsync(int projectId, DateTime? reportDate)
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return RedirectToPage("/Account/Login");

        var project = await _context.Project.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId && !p.IsDeleted);
        if (project == null)
        {
            TempData["ErrorMessage"] = "项目不存在或已删除";
            return RedirectToPage("./Index", new { tab = Tab });
        }
        var isProjectAdmin = await _context.ProjectUsers.AsNoTracking()
            .AnyAsync(pu => pu.ProjectId == project.Id && pu.UserId == currentUser.Id && pu.ProjectRole == (int)ProjectRole.Admin);
        if (!isProjectAdmin && currentUser.Role != UserRole.systemAdmin)
        {
            TempData["ErrorMessage"] = $"仅「{project.Name}」的项目管理员可生成团队日报";
            return RedirectToPage("./Index", new { tab = Tab });
        }

        var date = reportDate?.Date ?? AppTime.Today;
        try
        {
            var result = await _personalSummaries.GenerateTeamReportAsync(projectId, date);
            TempData["SuccessMessage"] = $"团队日报已处理（{date:yyyy-MM-dd}）：{result.ToDisplayText()}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate team report for project {ProjectId}", projectId);
            TempData["ErrorMessage"] = "团队日报生成失败，请稍后重试";
        }

        return RedirectToPage("./Index", new
        {
            tab = "team",
            filterProjectId = projectId,
            filterReportType = 4,
            filterEndDate = date,
            filterStartDate = date
        });
    }

    private async Task LoadFilterOptionsAsync(ApplicationUser currentUser)
    {
        if (string.Equals(Tab, "team", StringComparison.OrdinalIgnoreCase))
        {
            // 团队日报：下拉只列出当前用户作为项目管理员的项目（与团队汇报的可见范围一致）
            IQueryable<Project> projectQuery = _context.Project.AsNoTracking()
                .Where(p => !p.IsDeleted && p.Status == ProjectStatus.Active);
            if (currentUser.Role != UserRole.systemAdmin)
            {
                var adminProjectIds = await _context.ProjectUsers.AsNoTracking()
                    .Where(pu => pu.UserId == currentUser.Id && pu.ProjectRole == (int)ProjectRole.Admin)
                    .Select(pu => pu.ProjectId)
                    .ToListAsync();
                projectQuery = projectQuery.Where(p => adminProjectIds.Contains(p.Id));
            }
            ProjectOptions = await projectQuery
                .OrderBy(p => p.Name)
                .Select(p => new SelectListItem(p.Name, p.Id.ToString()))
                .ToListAsync();
        }
        else
        {
            ProjectOptions = (await _service.GetAccessibleProjects(currentUser))
                .Select(project => new SelectListItem(project.Text, project.Value))
                .ToList();
        }

        ReportTypeOptions = new List<SelectListItem>
        {
            new("日报", "1"),
            new("周报", "2"),
            new("月报", "3"),
            new("团队汇报", "4")
        };
    }

    private async Task LoadLeaderProjectsAsync(ApplicationUser currentUser)
    {
        IQueryable<Project> query = _context.Project.AsNoTracking()
            .Where(p => !p.IsDeleted && p.Status == ProjectStatus.Active);
        if (currentUser.Role != UserRole.systemAdmin)
        {
            // 项目管理员（ProjectRole.Admin）可以生成团队汇报（负责人同时也是管理员）
            var adminProjectIds = await _context.ProjectUsers.AsNoTracking()
                .Where(pu => pu.UserId == currentUser.Id && pu.ProjectRole == (int)ProjectRole.Admin)
                .Select(pu => pu.ProjectId)
                .ToListAsync();
            query = query.Where(p => adminProjectIds.Contains(p.Id));
        }

        var projects = await query.OrderBy(p => p.Name).ToListAsync();
        IsAnyProjectLeader = projects.Count > 0;
        LeaderProjectOptions = projects
            .Select(p => new SelectListItem(p.Name, p.Id.ToString()))
            .ToList();
    }
}

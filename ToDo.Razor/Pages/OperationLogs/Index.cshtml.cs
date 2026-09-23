using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Identity;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.OperationLogs;

public class IndexModel : PageModel
{
    private readonly OperationLogService _logService;
    private readonly UserManager<ApplicationUser> _userManager;

    public IndexModel(OperationLogService logService, UserManager<ApplicationUser> userManager)
    {
        _logService = logService;
        _userManager = userManager;
    }

    public PaginatedList<ChangeLog> ChangeLogs { get; set; } = null!;

    // 添加这两个属性来存储下拉选项
    public List<SelectListItem> UserOptions { get; set; } = new();
    public List<SelectListItem> ProjectOptions { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public OperationLogService.LogQueryParameters Parameters { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        if (!await _logService.CanViewLogsAsync(user)) return Forbid();

        // 从数据库获取用户和项目列表
        var users = await _logService.GetDistinctUsersAsync(user);
        var projects = await _logService.GetDistinctProjectsAsync(user);

        // 构建下拉选项
        UserOptions = users.Select(u => new SelectListItem
        {
            Text = u,
            Value = u,
            Selected = u == Parameters.UserName
        }).ToList();

        UserOptions.Insert(0, new SelectListItem("全部", ""));

        ProjectOptions = projects.Select(p => new SelectListItem
        {
            Text = p,
            Value = p,
            Selected = p == Parameters.ProjectName
        }).ToList();

        ProjectOptions.Insert(0, new SelectListItem("全部", ""));

        ChangeLogs = await _logService.GetPagedLogsAsync(Parameters, user);
        return Page();
    }
}

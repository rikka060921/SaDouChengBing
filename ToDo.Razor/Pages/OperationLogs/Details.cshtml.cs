using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Identity;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.OperationLogs;

public class DetailsModel : PageModel
{
    private readonly OperationLogService _logService;
    private readonly UserManager<ApplicationUser> _userManager;

    public DetailsModel(OperationLogService logService, UserManager<ApplicationUser> userManager)
    {
        _logService = logService;
        _userManager = userManager;
    }

    public ChangeLog ChangeLog { get; set; } = null!;
    public ToDoTask? Task { get; set; }
    public int Index { get; set; }
    public string BeforeContent { get; set; } = string.Empty;
    public string AfterContent { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public OperationLogService.LogQueryParameters Parameters { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        if (!await _logService.CanViewLogsAsync(user)) return Forbid();

        var result = await _logService.GetLogDetailsAsync(id, Parameters, user);
        if (result == null) return NotFound();

        ChangeLog = result.Value.Log;
        Index = result.Value.Index;
        BeforeContent = result.Value.BeforeContent;
        AfterContent = result.Value.AfterContent;

        // 查询关联任务
        Task = await _logService.GetTaskDetailsAsync(ChangeLog.TaskId, user);

        return Page();
    }
}

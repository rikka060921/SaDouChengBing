using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.RedBlue;

[Authorize]
public class IndexModel : PageModel
{
    private readonly RedBlueService _service;
    private readonly UserManager<ApplicationUser> _userManager;

    public IndexModel(RedBlueService service, UserManager<ApplicationUser> userManager)
    {
        _service = service;
        _userManager = userManager;
    }

    public List<RedBlueSession> Sessions { get; set; } = new();
    public List<Project> Projects { get; set; } = new();
    [BindProperty(SupportsGet = true)] public int? ProjectId { get; set; }
    [BindProperty] public string Topic { get; set; } = string.Empty;
    [BindProperty] public string Objective { get; set; } = string.Empty;
    [BindProperty] public int MaxRounds { get; set; } = 3;

    public async Task OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return;
        Projects = await _service.GetAccessibleProjectsAsync(user);
        if (!ProjectId.HasValue && Projects.Count == 1) ProjectId = Projects[0].Id;
        Sessions = ProjectId.HasValue
            ? await _service.GetRecentAsync(ProjectId)
            : new List<RedBlueSession>();
        if (ProjectId.HasValue)
        {
            var suggestion = await _service.SuggestSetupAsync(ProjectId.Value, user);
            if (suggestion != null)
            {
                Topic = suggestion.Topic;
                Objective = suggestion.Objective;
                MaxRounds = suggestion.MaxRounds;
            }
        }
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        if (string.IsNullOrWhiteSpace(Topic)) ModelState.AddModelError(nameof(Topic), "请输入主题");
        if (string.IsNullOrWhiteSpace(Objective)) ModelState.AddModelError(nameof(Objective), "请输入决策目标");
        if (!ProjectId.HasValue) ModelState.AddModelError(nameof(ProjectId), "请选择当前项目");

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");
        if (ProjectId.HasValue && !await _service.CanAccessProjectAsync(ProjectId.Value, user)) return Forbid();
        if (!ModelState.IsValid)
        {
            Projects = await _service.GetAccessibleProjectsAsync(user);
            Sessions = ProjectId.HasValue
                ? await _service.GetRecentAsync(ProjectId)
                : new List<RedBlueSession>();
            return Page();
        }
        var session = await _service.CreateAsync(Topic, Objective, MaxRounds, user.Id, ProjectId);
        return RedirectToPage("./Details", new { id = session.Id });
    }
}

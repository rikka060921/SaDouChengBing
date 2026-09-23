using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.AiSessions;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class IndexModel : PageModel
{
    private readonly AiSessionService _sessions;
    private readonly UserManager<ApplicationUser> _userManager;

    public IndexModel(AiSessionService sessions, UserManager<ApplicationUser> userManager)
    {
        _sessions = sessions;
        _userManager = userManager;
    }

    public List<AiSession> Sessions { get; set; } = new();
    [BindProperty(SupportsGet = true)] public bool IncludeArchived { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");
        Sessions = await _sessions.GetRecentAsync(user, 200, IncludeArchived);
        return Page();
    }
}

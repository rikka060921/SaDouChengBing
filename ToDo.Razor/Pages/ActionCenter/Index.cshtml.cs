using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.ActionCenter;

[Authorize]
public sealed class IndexModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager,
    ApprovalRequestService approvals) : PageModel
{
    public IReadOnlyList<PersonalActionCard> Items { get; set; } = [];
    public int TotalPending { get; set; }
    public int UrgentCount { get; set; }
    public int MyActiveTaskCount { get; set; }
    public const int PageSize = 30;
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalPending / PageSize));

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        if (user.IsDeleted || user.Status != UserStatus.Active) return Forbid();
        MyActiveTaskCount = await new TaskProgressService(context).MyActiveTasks(user.Id).CountAsync(HttpContext.RequestAborted);
        var cards = await new PersonalActionService(context, approvals).GetAsync(user, AppTime.Now, HttpContext.RequestAborted);
        TotalPending = cards.Count;
        UrgentCount = cards.Count(c => c.Priority <= 1);
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);
        Items = cards.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
        return Page();
    }
}

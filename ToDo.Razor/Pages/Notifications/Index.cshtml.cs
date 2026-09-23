using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Notifications;

public class IndexModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly UserNotificationService _notifications;

    public List<UserNotification> Items { get; set; } = new();
    [BindProperty(SupportsGet = true)] public bool UnreadOnly { get; set; } = true;
    public int UnreadCount => Items.Count(item => !item.IsRead);

    public IndexModel(UserManager<ApplicationUser> userManager, UserNotificationService notifications)
    {
        _userManager = userManager;
        _notifications = notifications;
    }

    public async Task OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user != null) Items = await _notifications.GetForUserAsync(user.Id, UnreadOnly);
    }

    public async Task<IActionResult> OnPostReadAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user != null) await _notifications.MarkReadAsync(id, user.Id);
        return RedirectToPage(new { UnreadOnly });
    }

    public async Task<IActionResult> OnPostMarkAllReadAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user != null) await _notifications.MarkAllReadAsync(user.Id);
        return RedirectToPage(new { UnreadOnly });
    }

    public async Task<IActionResult> OnPostOpenAsync(int id, string? link)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user != null) await _notifications.MarkReadAsync(id, user.Id);
        return !string.IsNullOrWhiteSpace(link) && Url.IsLocalUrl(link)
            ? LocalRedirect(link)
            : RedirectToPage(new { UnreadOnly });
    }
}

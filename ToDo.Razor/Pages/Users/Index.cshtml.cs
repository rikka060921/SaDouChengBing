using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Users;

[Authorize(Roles = "systemAdmin")]
public class IndexModel : PageModel
{
    private readonly ApplicationUserDomainService _userService;
    private readonly Dictionary<int, bool> _canEdit = new();
    private readonly Dictionary<int, bool> _canToggle = new();
    private readonly Dictionary<int, bool> _canDelete = new();

    public IndexModel(ApplicationUserDomainService userService)
    {
        _userService = userService;
    }

    public IList<ApplicationUser> Users { get; set; } = new List<ApplicationUser>();
    public bool IsAdmin => true;
    public int CurrentUserId { get; set; }
    public bool ShowDeleted { get; set; }

    [BindProperty(SupportsGet = true)] public string? SearchString { get; set; }
    [BindProperty(SupportsGet = true)] public string? GenderFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? StatusFilter { get; set; }
    [BindProperty(SupportsGet = true)] public int CurrentPage { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = 10;

    public int TotalPages { get; set; }
    public int TotalItems { get; set; }
    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;

    public bool CanEditUser(int targetUserId) => _canEdit.GetValueOrDefault(targetUserId);
    public bool CanToggleStatus(int targetUserId) => _canToggle.GetValueOrDefault(targetUserId);
    public bool CanDeleteUser(int targetUserId) => _canDelete.GetValueOrDefault(targetUserId);

    public async Task<IActionResult> OnGetAsync(bool showDeleted = false)
    {
        var currentUser = await _userService.GetCurrentUserAsync(User);
        if (currentUser == null) return Challenge();

        ShowDeleted = showDeleted;
        CurrentUserId = currentUser.Id;
        CurrentPage = Math.Max(1, CurrentPage);
        PageSize = Math.Clamp(PageSize, 5, 100);

        var (users, totalCount) = await _userService.GetSortedUsersAsync(
            SearchString ?? string.Empty,
            GenderFilter ?? string.Empty,
            StatusFilter ?? string.Empty,
            showDeleted,
            CurrentPage,
            PageSize);

        TotalItems = totalCount;
        TotalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));
        CurrentPage = Math.Clamp(CurrentPage, 1, TotalPages);
        Users = users;

        foreach (var user in Users)
        {
            _canEdit[user.Id] = await _userService.CanEditUserAsync(user.Id, currentUser.Id);
            _canToggle[user.Id] = await _userService.CanToggleUserStatusAsync(user.Id, currentUser.Id);
            _canDelete[user.Id] = await _userService.CanDeleteUserAsync(user.Id, currentUser.Id);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var currentUser = await _userService.GetCurrentUserAsync(User);
        if (currentUser == null) return Challenge();

        var result = await _userService.DeleteUserWithLogAsync(id, currentUser);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "用户已移入回收站"
            : result.Errors.FirstOrDefault()?.Description;
        return RedirectToPage("./Index");
    }

    public async Task<IActionResult> OnPostRestoreAsync(int id)
    {
        var currentUser = await _userService.GetCurrentUserAsync(User);
        if (currentUser == null) return Challenge();

        var result = await _userService.RestoreUserWithLogAsync(id, currentUser);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "用户恢复成功"
            : result.Errors.FirstOrDefault()?.Description;
        return RedirectToPage("./Index", new { showDeleted = true });
    }

    public async Task<IActionResult> OnPostToggleStatusAsync(int id)
    {
        var currentUser = await _userService.GetCurrentUserAsync(User);
        if (currentUser == null) return Challenge();

        var result = await _userService.ToggleUserStatusWithLogAsync(id, currentUser);
        TempData[result.Succeeded ? "SuccessMessage" : "ErrorMessage"] = result.Succeeded
            ? "用户状态已更新"
            : result.Errors.FirstOrDefault()?.Description;
        return RedirectToPage("./Index");
    }
}

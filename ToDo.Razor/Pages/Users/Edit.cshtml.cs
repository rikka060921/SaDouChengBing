using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities.Dto;

namespace ToDo.Razor.Pages.Users;

[Authorize]
public class EditModel : PageModel
{
    private readonly ApplicationUserDomainService _userService;

    public EditModel(ApplicationUserDomainService userService)
    {
        _userService = userService;
    }

    [BindProperty]
    public UserEditDto UserEditDto { get; set; } = new();

    public List<string> FinalRoles { get; set; } = new();
    public List<ProjectListDto> CreatedProjects { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var currentUser = await _userService.GetCurrentUserAsync(User);
        if (currentUser == null) return Challenge();
        if (!await _userService.CanEditUserAsync(id, currentUser.Id)) return Forbid();

        var user = await _userService.GetUserDetailsAsync(id);
        if (user == null) return NotFound();

        await LoadRelatedDataAsync(user.Id);
        UserEditDto = new UserEditDto
        {
            Id = user.Id,
            UserName = user.UserName ?? string.Empty,
            RealName = user.RealName ?? string.Empty,
            Email = user.Email ?? string.Empty,
            Gender = user.Gender,
            PhoneNumber = user.PhoneNumber ?? string.Empty,
            Role = user.Role
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var currentUser = await _userService.GetCurrentUserAsync(User);
        if (currentUser == null) return Challenge();
        if (!await _userService.CanEditUserAsync(UserEditDto.Id, currentUser.Id)) return Forbid();

        var targetUser = await _userService.GetUserDetailsAsync(UserEditDto.Id);
        if (targetUser == null) return NotFound();

        if (!ModelState.IsValid)
        {
            await LoadRelatedDataAsync(UserEditDto.Id);
            return Page();
        }

        if (!await _userService.HasUserChangesAsync(UserEditDto.Id, UserEditDto))
        {
            ModelState.AddModelError(string.Empty, "未检测到任何更改");
            await LoadRelatedDataAsync(UserEditDto.Id);
            return Page();
        }

        var result = await _userService.UpdateUserWithLogAsync(
            UserEditDto.Id,
            UserEditDto.RealName,
            UserEditDto.Email,
            UserEditDto.Gender,
            UserEditDto.PhoneNumber ?? string.Empty,
            currentUser);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            await LoadRelatedDataAsync(UserEditDto.Id);
            return Page();
        }

        TempData["SuccessMessage"] = "用户信息更新成功";
        return currentUser.Id == UserEditDto.Id
            ? RedirectToPage("/Account/Profile")
            : RedirectToPage("./Index");
    }

    private async Task LoadRelatedDataAsync(int userId)
    {
        var user = await _userService.GetUserDetailsAsync(userId);
        if (user == null) return;
        FinalRoles = await _userService.GetUserRolesAsync(user);
        CreatedProjects = await _userService.GetUserProjectsAsync(userId);
    }
}

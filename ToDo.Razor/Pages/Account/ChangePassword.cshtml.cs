using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities.Dto;

namespace ToDo.Razor.Pages.Account
{
    [Authorize]
    public class ChangePasswordModel : PageModel
    {
        private readonly ApplicationUserDomainService _userService;

        public ChangePasswordModel(ApplicationUserDomainService userService)
        {
            _userService = userService;
        }

        [BindProperty]
        public ChangePasswordDto ChangePasswordDto { get; set; } = new();

        public void OnGet()
        {
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
            {
                return Page();
            }

            var operatorUser = await _userService.GetCurrentUserAsync(User);
            if (operatorUser == null) return Challenge();
            var result = await _userService.ChangePasswordWithLogAsync(
                operatorUser.Id,
                ChangePasswordDto.CurrentPassword,
                ChangePasswordDto.NewPassword,
                operatorUser);

            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }
                return Page();
            }

            TempData["SuccessMessage"] = "密码修改成功";
            return RedirectToPage();
        }
    }
}

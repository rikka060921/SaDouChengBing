using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Users
{
    [Authorize(Roles = "systemAdmin")]
    public class RecycleBinModel : PageModel
    {
        private readonly ApplicationUserDomainService _userService;

        public RecycleBinModel(ApplicationUserDomainService userService)
        {
            _userService = userService;
        }

        public IList<ApplicationUser> DeletedUsers { get; set; } = new List<ApplicationUser>();

        public async Task OnGetAsync()
        {
            DeletedUsers = await _userService.GetDeletedUsersAsync();
        }

        public async Task<IActionResult> OnPostRestoreAsync(int id)
        {
            var currentUser = await _userService.GetCurrentUserAsync(User);
            if (currentUser == null) return Challenge();
            var result = await _userService.RestoreUserWithLogAsync(id, currentUser);

            if (!result.Succeeded)
            {
                TempData["ErrorMessage"] = "恢复用户失败: " + result.Errors.FirstOrDefault()?.Description;
            }
            else
            {
                TempData["SuccessMessage"] = "用户恢复成功";
            }

            return RedirectToPage("./RecycleBin");
        }

        public async Task<IActionResult> OnPostPermanentlyDeleteAsync(int id)
        {
            var currentUser = await _userService.GetCurrentUserAsync(User);
            if (currentUser == null) return Challenge();
            var result = await _userService.PermanentlyDeleteUserWithLogAsync(id, currentUser);

            if (!result.Succeeded)
            {
                TempData["ErrorMessage"] = "永久删除失败: " + result.Errors.FirstOrDefault()?.Description;
            }
            else
            {
                TempData["SuccessMessage"] = "用户已永久删除";
            }

            return RedirectToPage("./RecycleBin");
        }
    }
}

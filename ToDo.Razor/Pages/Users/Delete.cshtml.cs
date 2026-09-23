using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Users
{
    [Authorize(Roles = "systemAdmin")]
    public class DeleteModel : PageModel
    {
        private readonly ApplicationUserDomainService _userService;

        public DeleteModel(ApplicationUserDomainService userService)
        {
            _userService = userService;
        }

        [BindProperty]
        public ApplicationUser UserToDelete { get; set; } = null!;

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var userToDelete = await _userService.GetUserDetailsAsync(id);
            if (userToDelete == null || userToDelete.IsDeleted)
            {
                return NotFound();
            }
            UserToDelete = userToDelete;

            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            var currentUser = await _userService.GetCurrentUserAsync(User);
            if (currentUser == null) return Challenge();

            // 获取删除前的用户状态
            var userToDelete = await _userService.GetUserDetailsAsync(id);
            if (userToDelete == null || userToDelete.IsDeleted)
            {
                return NotFound();
            }

            var result = await _userService.DeleteUserWithLogAsync(id, currentUser);

            if (!result.Succeeded)
            {
                TempData["ErrorMessage"] = result.Errors.FirstOrDefault()?.Description;
            }
            else
            {
                TempData["SuccessMessage"] = "用户已移至回收站";
            }

            return RedirectToPage("./Index");
        }
    }
}

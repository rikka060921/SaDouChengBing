using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System;
using ToDo.Domain;
using ToDo.Entities;
using ToDo.Entities.Dto;

namespace ToDo.Razor.Pages.Account
{
    [Authorize]
    public class ProfileModel : PageModel
    {
        private readonly ApplicationUserDomainService _userService;

        public ProfileModel(ApplicationUserDomainService userService)
        {
            _userService = userService;
        }

        [BindProperty]
        public UserEditDto UserEditDto { get; set; } = new();

        public List<string> FinalRoles { get; set; } = new();

        public async Task<IActionResult> OnGetAsync()
        {
            var currentUser = await _userService.GetCurrentUserAsync(User);
            if (currentUser == null) return Redirect("/Account/Login");

            FinalRoles = await _userService.GetUserRolesAsync(currentUser);

            UserEditDto = new UserEditDto
            {
                Id = currentUser.Id,
                UserName = currentUser.UserName ?? string.Empty,
                RealName = currentUser.RealName ?? string.Empty,
                Email = currentUser.Email ?? string.Empty,
                Gender = currentUser.Gender,
                PhoneNumber = currentUser.PhoneNumber ?? string.Empty,
                Role = currentUser.Role
            };

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var currentUser = await _userService.GetCurrentUserAsync(User);
            if (currentUser == null) return Redirect("/Account/Login");

            UserEditDto.Email = UserEditDto.Email?.Trim() ?? string.Empty;
            UserEditDto.PhoneNumber = UserEditDto.PhoneNumber?.Trim();

            // 这些字段在个人中心是只读或展示字段，不应阻止邮箱和手机号保存。
            ModelState.Remove("UserEditDto.UserName");
            ModelState.Remove("UserEditDto.RealName");
            ModelState.Remove("UserEditDto.Gender");
            ModelState.Remove("UserEditDto.Role");
            ModelState.Remove("UserEditDto.Status");

            if (!ModelState.IsValid)
            {
                FinalRoles = await _userService.GetUserRolesAsync(currentUser);
                return Page();
            }

            var emailChanged = !string.Equals(
                currentUser.Email,
                UserEditDto.Email,
                StringComparison.OrdinalIgnoreCase);
            var phoneChanged = !string.Equals(
                currentUser.PhoneNumber,
                UserEditDto.PhoneNumber,
                StringComparison.Ordinal);

            if (!emailChanged && !phoneChanged)
            {
                TempData["InfoMessage"] = "没有检测到变更";
                FinalRoles = await _userService.GetUserRolesAsync(currentUser);
                return Page();
            }

            var result = await _userService.UpdateUserWithLogAsync(
                currentUser.Id,
                currentUser.RealName ?? string.Empty,
                UserEditDto.Email,
                currentUser.Gender,
                UserEditDto.PhoneNumber ?? string.Empty,
                currentUser);

            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error.Description);
                }

                FinalRoles = await _userService.GetUserRolesAsync(currentUser);
                return Page();
            }

            TempData["SuccessMessage"] = "个人资料更新成功";
            return RedirectToPage();
        }
    }
}

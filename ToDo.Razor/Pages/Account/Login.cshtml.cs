using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using ToDo.Entities;
using ToDo.Context;

namespace ToDo.Razor.Pages.Account
{
    public class LoginModel : PageModel
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<LoginModel> _logger;

        public LoginModel(
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            ILogger<LoginModel> logger)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _logger = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public string ReturnUrl { get; set; } = string.Empty;

        [TempData]
        public string? ErrorMessage { get; set; }

        public class InputModel
        {
            [Required(ErrorMessage = "用户名必填")]
            public string UserName { get; set; } = string.Empty;

            [Required(ErrorMessage = "密码必填")]
            [DataType(DataType.Password)]
            public string Password { get; set; } = string.Empty;

            [Display(Name = "记住我?")]
            public bool RememberMe { get; set; }
        }

        public void OnGet(string? returnUrl = null)
        {
            if (!string.IsNullOrEmpty(ErrorMessage))
            {
                ModelState.AddModelError(string.Empty, ErrorMessage);
            }

            ReturnUrl = returnUrl ?? Url.Content("~/");
        }

        public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
        {
            returnUrl ??= Url.Content("~/");

            if (ModelState.IsValid)
            {
                // 先查找用户
                var user = await _userManager.FindByNameAsync(Input.UserName);

                // 检查用户状态
                if (user != null && user.Status == UserStatus.Inactive)
                {
                    _logger.LogWarning($"尝试登录已被禁用的账号: {Input.UserName}");
                    ModelState.AddModelError(string.Empty, "您的账号已被禁用，请联系管理员");
                    return Page();
                }

                // 尝试登录
                var result = await _signInManager.PasswordSignInAsync(
                    Input.UserName,
                    Input.Password,
                    Input.RememberMe,
                    lockoutOnFailure: true);

                if (result.Succeeded)
                {
                    _logger.LogInformation("用户 {UserName} 已登录", Input.UserName);
                    return RedirectToPage("/ActionCenter/Index");
                }
                if (result.RequiresTwoFactor)
                {
                    return RedirectToPage("./LoginWith2fa", new { ReturnUrl = returnUrl });
                }
                if (result.IsLockedOut)
                {
                    _logger.LogWarning("用户账号被锁定: {UserName}", Input.UserName);
                    return RedirectToPage("./Lockout");
                }
                else
                {
                    _logger.LogWarning("无效的登录尝试: {UserName}", Input.UserName);
                    ModelState.AddModelError(string.Empty, "无效的登录尝试");
                    return Page();
                }
            }

            return Page();
        }
    }
}

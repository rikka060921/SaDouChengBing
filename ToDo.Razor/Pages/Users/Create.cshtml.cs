using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Users
{
    [Authorize(Roles = "systemAdmin")]
    public class CreateModel : PageModel
    {
        private readonly ApplicationUserDomainService _userService;

        public CreateModel(ApplicationUserDomainService userService)
        {
            _userService = userService;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public class InputModel
        {
            [Required(ErrorMessage = "用户名是必填项")]
            [Display(Name = "用户名")]
            public string UserName { get; set; } = string.Empty;

            [Required(ErrorMessage = "真实姓名是必填项")]
            [Display(Name = "真实姓名")]
            public string RealName { get; set; } = string.Empty;

            [Required(ErrorMessage = "邮箱是必填项")]
            [EmailAddress(ErrorMessage = "请输入有效的邮箱地址")]
            [Display(Name = "邮箱")]
            public string Email { get; set; } = string.Empty;

            [Required(ErrorMessage = "请选择性别")]
            [Display(Name = "性别")]
            public Gender Gender { get; set; }

            [Required(ErrorMessage = "请选择用户角色")]
            [Display(Name = "用户角色")]
            public UserRole Role { get; set; }

            [Required(ErrorMessage = "电话号码是必填项")]
            [Phone(ErrorMessage = "请输入有效的电话号码")]
            [RegularExpression(@"^1[3-9]\d{9}$", ErrorMessage = "请输入11位中国手机号码")]
            [Display(Name = "电话号码")]
            public string PhoneNumber { get; set; } = string.Empty;

            [Required(ErrorMessage = "密码是必填项")]
            [StringLength(100, ErrorMessage = "密码长度至少为 {2} 个字符", MinimumLength = 6)]
            [DataType(DataType.Password)]
            [Display(Name = "密码")]
            public string Password { get; set; } = string.Empty;


            [DataType(DataType.Password)]
            [Display(Name = "确认密码")]
            [Required(ErrorMessage = "确认密码是必填项")]  // 添加这行
            [Compare("Password", ErrorMessage = "密码和确认密码不匹配")]
            public string ConfirmPassword { get; set; } = string.Empty;
        }

        public void OnGet()
        {
        }

        // 修改 OnPostAsync 方法
        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
            {
                return Page();
            }

            var currentUser = await _userService.GetCurrentUserAsync(User);
            if (currentUser == null) return Challenge();
            var (result, user) = await _userService.CreateUserWithLogAsync(
                Input.UserName,
                Input.RealName,
                Input.Email,
                Input.Gender,
                Input.Role,
                Input.PhoneNumber,
                Input.Password,
                currentUser);

            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                {
                    // 将用户名重复的错误消息改为中文
                    if (error.Code == "DuplicateUserName")
                    {
                        ModelState.AddModelError(string.Empty, $"用户名 '{Input.UserName}' 已被占用。");
                    }
                    else
                    {
                        ModelState.AddModelError(string.Empty, error.Description);
                    }
                }
                return Page();
            }

            TempData["SuccessMessage"] = $"用户 {user.UserName} 创建成功";
            return RedirectToPage("./Index");
        }
    }
}

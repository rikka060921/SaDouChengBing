using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Threading.Tasks;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Projects
{
    [Authorize]
    public class CreateModel : PageModel
    {
        private readonly ProjectDomain _projectDomain;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly ILogger<CreateModel> _logger;

        public CreateModel(
            ProjectDomain projectDomain,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ILogger<CreateModel> logger)
        {
            _projectDomain = projectDomain;
            _userManager = userManager;
            _signInManager = signInManager;
            _logger = logger;
        }

        [BindProperty]
        public ProjectCreateDto Project { get; set; } = new();

        [BindProperty]
        [Display(Name = "项目可见性")]
        public char IsEncrypted { get; set; } = '0';

        public void OnGet()
        {
            // 初始化默认值
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
            {
                // 收集所有验证错误信息
                var errorMessages = ModelState.Values
                    .SelectMany(v => v.Errors)
                    .Select(e => e.ErrorMessage)
                    .ToList();

                TempData["ErrorMessage"] = string.Join("；", errorMessages);
                return Page();
            }

            if (IsEncrypted is not ('0' or '1' or '2'))
            {
                ModelState.AddModelError(nameof(IsEncrypted), "项目可见性无效");
                TempData["ErrorMessage"] = "项目可见性无效";
                return Page();
            }

            var user = await _userManager.GetUserAsync(User);
            if (user == null || !_signInManager.IsSignedIn(User))
            {
                TempData["ErrorMessage"] = "用户未登录或会话已过期";
                return RedirectToPage("/Account/Login");
            }

            try
            {
                // 检查项目名称是否已存在
                bool projectExists = await _projectDomain.ProjectNameExistsAsync(Project.Name, user.Id);
                if (projectExists)
                {
                    TempData["ErrorMessage"] = $"项目名称【{Project.Name}】已存在，请使用其他名称";
                    return Page();
                }

                var requirementsJson = Request.Form["RequirementsJson"];
                var requirements = !string.IsNullOrEmpty(requirementsJson)
                    ? string.Join("\n", JsonSerializer.Deserialize<List<string>>(requirementsJson!) ?? [])
                    : Project.Requirements;

                var projectId = await _projectDomain.CreateProjectAsync(
                    projectName: Project.Name,
                    description: Project.Description ?? string.Empty,
                    currentUserId: user.Id,
                    requirements: requirements,
                    isEncrypted: IsEncrypted
                );

                if (projectId > 0)
                {
                    // 创建成功，重定向到项目列表页面并显示成功消息
                    TempData["SuccessMessage"] = $"项目【{Project.Name}】创建成功！";
                    return RedirectToPage("./Index");
                }
                else
                {
                    TempData["ErrorMessage"] = "项目创建失败，请重试";
                    return Page();
                }
            }
            catch (ArgumentException ex)
            {
                TempData["ErrorMessage"] = ex.Message;
                _logger.LogWarning(ex, "项目创建参数验证失败");
                return Page();
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = "项目创建失败，请稍后重试";
                _logger.LogError(ex, "用户 {UserId} 创建项目失败", user?.Id);
                return Page();
            }
        }

        public async Task<JsonResult> OnPostCheckProjectNameAsync(string projectName)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return new JsonResult(new { exists = true });
            }

            bool exists = await _projectDomain.ProjectNameExistsAsync(projectName, user.Id);
            return new JsonResult(new { exists });
        }
    }

    public class ProjectCreateDto
    {
        [Required(ErrorMessage = "项目名称不能为空")]
        [StringLength(255, ErrorMessage = "项目名称不能超过{1}个字符")]
        [Display(Name = "项目名称")]
        public string Name { get; set; } = string.Empty;

        [StringLength(1000, ErrorMessage = "项目描述不能超过{1}个字符")]
        [Display(Name = "项目描述")]
        public string? Description { get; set; } = string.Empty;

        [StringLength(1000, ErrorMessage = "需求对象不能超过{1}个字符")]
        [Display(Name = "需求对象")]
        public string Requirements { get; set; } = string.Empty;
    }
}

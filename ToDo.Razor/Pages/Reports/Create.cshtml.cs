using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Identity;
using ToDo.Entities;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Http;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using ToDo.Domain;
using System.ComponentModel.DataAnnotations;

namespace ToDo.Razor.Pages.Reports
{
    public class CreateModel : PageModel
    {
        private readonly IDailyReportService _service;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IWebHostEnvironment _webHostEnv;
        private readonly ILogger<CreateModel> _logger;

        public CreateModel(
            IDailyReportService service,
            UserManager<ApplicationUser> userManager,
            IWebHostEnvironment webHostEnv,
            ILogger<CreateModel> logger)
        {
            _service = service;
            _userManager = userManager;
            _webHostEnv = webHostEnv;
            _logger = logger;
        }

        // 使用输入模型进行验证（新增）
        [BindProperty]
        public DailyReportInput DailyReport { get; set; } = new();

        [BindProperty]
        public List<IFormFile> Attachments { get; set; } = new();

        public List<SelectListItem> ProjectOptions { get; set; } = new();
        public List<SelectListItem> ReportTypeOptions { get; set; } = new();

        // 日报输入模型（包含标题验证）
        public class DailyReportInput
        {
            public int Id { get; set; }

            [Required(ErrorMessage = "请选择项目")]
            public int ProjectId { get; set; }

            [Required(ErrorMessage = "请选择报告类型")]
            [Range(1, 3, ErrorMessage = "报告类型无效")]
            public int ReportType { get; set; }

            [Required(ErrorMessage = "请选择报告日期")]
            public DateTime ReportDate { get; set; }

            [Required(ErrorMessage = "报告标题不能为空")]
            [CustomValidation(typeof(DailyReportInput), "ValidateTitleNotWhitespace")]
            public string ReportTitle { get; set; } = "";

            [Required(ErrorMessage = "报告内容不能为空")]
            public string ReportContent { get; set; } = "";

            // 自定义验证：标题不能为全空格
            public static ValidationResult? ValidateTitleNotWhitespace(string? title, ValidationContext context)
            {
                if (string.IsNullOrWhiteSpace(title))
                {
                    return new ValidationResult("报告标题不能为空白（包括全空格）");
                }
                return ValidationResult.Success;
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null)
            {
                return RedirectToPage("/Account/Login", new { returnUrl = "/Reports/Create" });
            }

            // 通过服务获取项目选项
            var domainProjects = await _service.GetAccessibleProjects(currentUser);
            ProjectOptions = domainProjects
                .Select(p => new SelectListItem(p.Text, p.Value))
                .ToList();

            // 报告类型选项
            ReportTypeOptions = new List<SelectListItem>
            {
                new SelectListItem("日报", "1", selected: true),
                new SelectListItem("周报", "2"),
                new SelectListItem("月报", "3")
            };

            DailyReport.ReportDate = AppTime.Today;
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var currentUser = await _userManager.GetUserAsync(User);

            try
            {
                if (currentUser == null)
                {
                    ModelState.AddModelError("", "请先登录");
                    await LoadOptions(currentUser);
                    return Page();
                }
                // 1. 模型验证（包含标题非空和非全空格验证）
                if (!ModelState.IsValid)
                {
                    await LoadOptions(currentUser);
                    return Page();
                }

                // 2. 验证项目存在
                var project = await _service.GetDailyReportProject(DailyReport.ProjectId);
                if (project == null)
                {
                    ModelState.AddModelError("DailyReport.ProjectId", "选择的项目不存在或已被删除");
                    await LoadOptions(currentUser);
                    return Page();
                }

                // 3. 验证权限
                var (hasPermission, errorMsg) = await _service.CheckReportPermission(currentUser, project);
                if (!hasPermission)
                {
                    ModelState.AddModelError("", errorMsg);
                    await LoadOptions(currentUser);
                    return Page();
                }

                // 4. 验证报告日期不能超过今天
                if (DailyReport.ReportDate > AppTime.Today)
                {
                    ModelState.AddModelError("DailyReport.ReportDate", "报告日期不能超过今天");
                    await LoadOptions(currentUser);
                    return Page();
                }

                // 5. 附件数量验证
                if (Attachments.Count > 5)
                {
                    ModelState.AddModelError("", "最多只能上传5个文件");
                    await LoadOptions(currentUser);
                    return Page();
                }

                // 6. 附件大小和格式验证（新增详细验证）
                foreach (var file in Attachments)
                {
                    if (file.Length > 10 * 1024 * 1024)
                    {
                        ModelState.AddModelError("", $"文件「{file.FileName}」超过10MB限制");
                        await LoadOptions(currentUser);
                        return Page();
                    }

                    var allowedExts = new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".jpg", ".jpeg", ".png", ".txt" };
                    var ext = Path.GetExtension(file.FileName).ToLower();
                    if (!allowedExts.Contains(ext))
                    {
                        ModelState.AddModelError("", $"文件「{file.FileName}」格式不支持");
                        await LoadOptions(currentUser);
                        return Page();
                    }
                }

                // 7. 生成标题（如果用户未输入，尽管验证已确保标题非空，但保留自动生成逻辑）
                if (string.IsNullOrWhiteSpace(DailyReport.ReportTitle))
                {
                    string typeText = DailyReport.ReportType switch
                    {
                        1 => "日报",
                        2 => "周报",
                        3 => "月报",
                        _ => ""
                    };
                    string formattedDate = DailyReport.ReportDate.ToString("yyyyMMdd");
                    DailyReport.ReportTitle = $"{currentUser.UserName}_{formattedDate}_{project.Name}_{typeText}";
                }

                // 8. 转换为实体对象
                var reportEntity = new DailyReport
                {
                    ProjectId = DailyReport.ProjectId,
                    ReportType = DailyReport.ReportType,
                    ReportDate = DailyReport.ReportDate,
                    ReportTitle = DailyReport.ReportTitle,
                    ReportContent = DailyReport.ReportContent,
                    ReporterId = currentUser.Id,
                    CreatedAt = AppTime.Now,
                    LastModifiedAt = AppTime.Now,
                    IsDeleted = false
                };

                // 9. 保存日报及附件
                await _service.SaveDailyReport(reportEntity, Attachments, _webHostEnv.WebRootPath);

                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create report for project {ProjectId}", DailyReport.ProjectId);
                ModelState.AddModelError("", "保存失败，请稍后重试");
                await LoadOptions(await _userManager.GetUserAsync(User));
                return Page();
            }
        }

        private async Task LoadOptions(ApplicationUser? currentUser)
        {
            if (currentUser != null)
            {
                var domainProjects = await _service.GetAccessibleProjects(currentUser);
                ProjectOptions = domainProjects
                    .Select(p => new SelectListItem(p.Text, p.Value))
                    .ToList();
            }
            ReportTypeOptions = new List<SelectListItem>
            {
                new SelectListItem("日报", "1", selected: DailyReport.ReportType == 1),
                new SelectListItem("周报", "2", selected: DailyReport.ReportType == 2),
                new SelectListItem("月报", "3", selected: DailyReport.ReportType == 3)
            };
        }
    }
}

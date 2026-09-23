using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ToDo.Entities;
using ToDo.Domain;

namespace ToDo.Razor.Pages.Reports
{
    public class EditModel : PageModel
    {
        private readonly IDailyReportService _service;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IWebHostEnvironment _webHostEnv;
        private readonly ILogger<EditModel> _logger;

        public EditModel(
            IDailyReportService service,
            UserManager<ApplicationUser> userManager,
            IWebHostEnvironment webHostEnv,
            ILogger<EditModel> logger)
        {
            _service = service;
            _userManager = userManager;
            _webHostEnv = webHostEnv;
            _logger = logger;
        }

        [BindProperty]
        public DailyReportInput Input { get; set; } = new();

        [BindProperty]
        public List<IFormFile> NewAttachments { get; set; } = new();

        [BindProperty]
        [DisplayFormat(ConvertEmptyStringToNull = false)]
        public string DeletedAttachmentIds { get; set; } = "";

        public List<SelectListItem> ProjectOptions { get; set; } = new();
        public List<SelectListItem> ReportTypeOptions { get; set; } = new();
        public string ErrorDetails { get; set; } = "";
        public bool IsReadOnly { get; private set; }
        public DateTime ReportCreatedAt { get; set; }
        public DateTime ReportLastModifiedAt { get; set; }
        public List<AttachmentViewModel> ExistingAttachments { get; set; } = new();
        public List<SelectListItem> ReporterOptions { get; set; } = new List<SelectListItem>();

        public class DailyReportInput
        {
            public int Id { get; set; }

            [Required(ErrorMessage = "必须选择项目")]
            public int ProjectId { get; set; }

            [Required(ErrorMessage = "必须选择报告类型")]
            [Range(1, 3, ErrorMessage = "请选择有效的报告类型")]
            public int ReportType { get; set; }

            [Required(ErrorMessage = "必须填写报告日期")]
            public DateTime ReportDate { get; set; }

            [Required(ErrorMessage = "报告标题不能为空")]
            public string ReportTitle { get; set; } = "";

            [Required(ErrorMessage = "报告内容不能为空")]
            public string ReportContent { get; set; } = "";

            [Required(ErrorMessage = "必须指定上报人")]
            public int ReporterId { get; set; }
        }

        public class AttachmentViewModel
        {
            public int Id { get; set; }
            public string FileName { get; set; } = "";
            public string FilePath { get; set; } = "";
            public string FileSizeFormatted { get; set; } = "";
            public DateTime UploadedAt { get; set; }
            public string IconClass { get; set; } = "";
        }

        public async Task<IActionResult> OnGetAsync(int? id)
        {
            try
            {
                var currentUser = await _userManager.GetUserAsync(User);
                if (currentUser == null)
                {
                    return RedirectToPage("/Account/Login", new { returnUrl = $"/Reports/Edit/{id}" });
                }

                await LoadDependenciesAsync(currentUser);

                if (id.HasValue)
                {
                    if (!await _service.CanAccessReportAsync(id.Value, currentUser))
                        return Forbid();

                    // 获取原始日报
                    var report = await _service.GetOriginalDailyReport(id.Value);
                    if (report == null)
                    {
                        return NotFound("日报不存在或已被删除");
                    }

                    // 检查编辑权限（核心：仅创建者24小时内可编辑）
                    var (canEdit, errorMsg) = await _service.CheckEditPermission(report, currentUser);
                    IsReadOnly = !canEdit;
                    if (IsReadOnly)
                    {
                        ErrorDetails = errorMsg; // 显示无权限原因
                    }

                    // 填充表单数据
                    Input = new DailyReportInput
                    {
                        Id = report.Id,
                        ProjectId = report.ProjectId,
                        ReportType = report.ReportType,
                        ReportDate = report.ReportDate,
                        ReportTitle = report.ReportTitle ?? "",
                        ReportContent = report.ReportContent ?? "",
                        ReporterId = report.ReporterId
                    };

                    ReportCreatedAt = report.CreatedAt;
                    ReportLastModifiedAt = report.LastModifiedAt;

                    // 加载现有附件
                    ExistingAttachments = report.Attachments.Select(att => new AttachmentViewModel
                    {
                        Id = att.Id,
                        FileName = att.FileName ?? "",
                        FilePath = att.FilePath ?? "",
                        FileSizeFormatted = FormatFileSize(att.FileSize),
                        UploadedAt = att.UploadedAt,
                        IconClass = GetFileIconClass(att.FileName ?? "")
                    }).ToList();
                }
                else
                {
                    // 新建日报默认值
                    Input.ReportDate = AppTime.Today;
                    Input.ReporterId = currentUser.Id;
                    ReportCreatedAt = AppTime.Now;
                    ReportLastModifiedAt = AppTime.Now;
                    IsReadOnly = false;
                }

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load report {ReportId} for editing", id);
                ErrorDetails = "加载页面失败，请稍后重试";
                return Page();
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            try
            {
                var currentUser = await _userManager.GetUserAsync(User);
                if (currentUser == null)
                {
                    ErrorDetails = "请先登录";
                    await LoadDependenciesAsync(currentUser);
                    return Page();
                }

                if (!ModelState.IsValid)
                {
                    var errors = ModelState.Values.SelectMany(v => v.Errors)
                                               .Select(e => e.ErrorMessage)
                                               .Where(m => !string.IsNullOrEmpty(m));
                    ErrorDetails = "表单验证失败: " + string.Join("；", errors);
                    await LoadDependenciesAsync(currentUser);
                    return Page();
                }

                // 获取原始日报
                if (!await _service.CanAccessReportAsync(Input.Id, currentUser))
                    return Forbid();

                var original = await _service.GetOriginalDailyReport(Input.Id);
                if (original == null)
                {
                    ErrorDetails = "日报不存在或已被删除";
                    await LoadDependenciesAsync(currentUser);
                    return Page();
                }

                // 再次校验权限（防前端绕过）
                var (canEdit, errorMsg) = await _service.CheckEditPermission(original, currentUser);
                if (!canEdit)
                {
                    ErrorDetails = errorMsg;
                    await LoadDependenciesAsync(currentUser);
                    return Page();
                }

                // 处理删除的附件
                var deletedIds = (DeletedAttachmentIds ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(value => int.TryParse(value, out var attachmentId) ? (int?)attachmentId : null)
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value)
                    .Distinct()
                    .ToList();
                await _service.DeleteAttachments(deletedIds, original, _webHostEnv.WebRootPath, currentUser);

                // 处理新附件（验证数量）
                if (NewAttachments.Count > 5)
                {
                    ErrorDetails = "最多上传5个附件";
                    await LoadDependenciesAsync(currentUser);
                    return Page();
                }
                await _service.AddNewAttachments(NewAttachments, original, _webHostEnv.WebRootPath, currentUser);

                // 更新日报内容
                await _service.UpdateDailyReport(
                    original,
                    Input.ReportTitle,
                    Input.ReportContent,
                    Input.ReportType,
                    Input.ReportDate ,
                    currentUser
                );

                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save report {ReportId}", Input.Id);
                ErrorDetails = "保存失败，请稍后重试";
                await LoadDependenciesAsync(await _userManager.GetUserAsync(User));
                return Page();
            }
        }

        private async Task LoadDependenciesAsync(ApplicationUser? currentUser)
        {
            if (currentUser == null) return;

            // 加载项目选项
            var domainProjects = await _service.GetAccessibleProjects(currentUser, activeOnly: true);
            ProjectOptions = domainProjects
                .Select(p => new SelectListItem(p.Text, p.Value))
                .ToList();

            // 加载报告类型选项
            ReportTypeOptions = new List<SelectListItem>
            {
                new SelectListItem("日报", "1"),
                new SelectListItem("周报", "2"),
                new SelectListItem("月报", "3")
            };

            // 加载上报人选项（仅当前用户，符合权限逻辑）
            ReporterOptions.Add(new SelectListItem(
                $"{currentUser.RealName}（{currentUser.UserName}）",
                currentUser.Id.ToString()
            ));
        }

        // 工具方法：格式化文件大小
        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }

        // 工具方法：获取文件图标样式
        private static string GetFileIconClass(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLower();
            return ext switch
            {
                ".pdf" => "fa-file-pdf text-danger",
                ".doc" or ".docx" => "fa-file-word text-primary",
                ".xls" or ".xlsx" => "fa-file-excel text-success",
                ".ppt" or ".pptx" => "fa-file-powerpoint text-warning",
                ".jpg" or ".jpeg" or ".png" => "fa-file-image text-info",
                ".txt" => "fa-file-alt text-secondary",
                _ => "fa-file text-dark"
            };
        }
    }
}

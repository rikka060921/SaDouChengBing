using Markdig;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Reports;

[Authorize]
public class DetailsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly IDailyReportService _service;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWebHostEnvironment _environment;

    public DetailsModel(
        ApplicationDbContext context,
        IDailyReportService service,
        UserManager<ApplicationUser> userManager,
        IWebHostEnvironment environment)
    {
        _context = context;
        _service = service;
        _userManager = userManager;
        _environment = environment;
    }

    public DailyReport DailyReport { get; set; } = null!;
    public Project Project { get; set; } = null!;
    public string ReporterName { get; set; } = string.Empty;
    public List<AttachmentViewModel> Attachments { get; set; } = new();
    public string ReportContentHtml => Markdown.ToHtml(
        DailyReport?.ReportContent ?? string.Empty,
        new MarkdownPipelineBuilder().DisableHtml().UseAdvancedExtensions().Build());

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (!id.HasValue) return NotFound();

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return Challenge();
        if (!await _service.CanAccessReportAsync(id.Value, currentUser)) return Forbid();

        var report = await _service.GetDailyReportWithDetails(id.Value, currentUser);
        if (report == null) return NotFound("日报不存在或已被删除");
        DailyReport = report;

        Project = DailyReport.Project!;
        ReporterName = await _service.GetReporterName(DailyReport.ReporterId);
        Attachments = DailyReport.Attachments
            .Where(attachment => !attachment.IsDeleted)
            .Select(attachment => new AttachmentViewModel
            {
                Id = attachment.Id,
                FileName = attachment.FileName,
                FileSize = attachment.FileSize,
                FileSizeFormatted = FormatFileSize(attachment.FileSize),
                UploadedAt = attachment.UploadedAt,
                IconClass = GetFileIconClass(attachment.FileName)
            })
            .ToList();

        return Page();
    }

    public async Task<IActionResult> OnGetDownloadAsync(int attachmentId)
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return Challenge();

        var attachment = await _context.DailyReportAttachments
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == attachmentId && !item.IsDeleted);
        if (attachment == null) return NotFound();
        if (!await _service.CanAccessReportAsync(attachment.DailyReportId, currentUser)) return Forbid();

        var reportUploadRoot = Path.GetFullPath(Path.Combine(_environment.WebRootPath, "uploads", "reports"));
        var physicalPath = Path.GetFullPath(Path.Combine(
            _environment.WebRootPath,
            (attachment.FilePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)));
        if (!physicalPath.StartsWith(reportUploadRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !System.IO.File.Exists(physicalPath))
            return NotFound();

        return PhysicalFile(
            physicalPath,
            string.IsNullOrWhiteSpace(attachment.ContentType) ? "application/octet-stream" : attachment.ContentType,
            attachment.FileName);
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024):F1} MB";
    }

    private static string GetFileIconClass(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
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

    public sealed class AttachmentViewModel
    {
        public int Id { get; set; }
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string FileSizeFormatted { get; set; } = string.Empty;
        public DateTime UploadedAt { get; set; }
        public string IconClass { get; set; } = string.Empty;
    }
}

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.MeetingMinutes;

public class EditModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly IMeetingMinutesService _service;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWebHostEnvironment _webHostEnv;
    private readonly MeetingTaskSyncService _taskSyncService;
    private readonly IEventBus _eventBus;

    public EditModel(
        ApplicationDbContext context,
        IMeetingMinutesService service,
        UserManager<ApplicationUser> userManager,
        IWebHostEnvironment webHostEnv,
        MeetingTaskSyncService taskSyncService,
        IEventBus eventBus)
    {
        _context = context;
        _service = service;
        _userManager = userManager;
        _webHostEnv = webHostEnv;
        _taskSyncService = taskSyncService;
        _eventBus = eventBus;
    }

    [BindProperty] public MeetingMinutesInput Input { get; set; } = new();
    [BindProperty] public List<IFormFile> NewAttachments { get; set; } = new();
    [BindProperty] public string? DeletedAttachmentIds { get; set; }
    public ToDo.Entities.MeetingMinutes MeetingMinutes { get; set; } = new();
    public List<DomainSelectListItem> ProjectOptions { get; set; } = new();
    public string Error { get; set; } = string.Empty;
    public bool IsReadOnly { get; private set; }
    public List<AttachmentViewModel> ExistingAttachments { get; set; } = new();
    public DateTime MeetingCreatedAt { get; set; }
    public DateTime MeetingLastModifiedAt { get; set; }

    public class MeetingMinutesInput
    {
        public int Id { get; set; }
        [Required(ErrorMessage = "会议标题不能为空")] public string MeetingTitle { get; set; } = string.Empty;
        [Required(ErrorMessage = "会议内容不能为空")] public string MeetingContent { get; set; } = string.Empty;
        public string? TranscriptText { get; set; }
        public int ProjectId { get; set; }
        public bool IsDraft { get; set; }
    }

    public class AttachmentViewModel
    {
        public int Id { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string FileSizeFormatted { get; set; } = string.Empty;
        public DateTime UploadedAt { get; set; }
        public string IconClass { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return RedirectToPage("/Account/Login", new { returnUrl = $"/MeetingMinutes/Edit/{id}" });
        var meeting = await _service.GetOriginalMeetingMinutes(id);
        if (meeting == null) return NotFound("会议纪要不存在或已被删除");
        MeetingMinutes = meeting;
        Input = new MeetingMinutesInput
        {
            Id = MeetingMinutes.Id,
            MeetingTitle = MeetingMinutes.MeetingTitle,
            MeetingContent = MeetingMinutes.MeetingContent,
            TranscriptText = MeetingMinutes.TranscriptText,
            ProjectId = MeetingMinutes.ProjectId,
            IsDraft = MeetingMinutes.IsDraft
        };
        Error = await LoadPageStateAsync(MeetingMinutes, currentUser);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return Forbid();
        if (Input.IsDraft) ModelState.Remove("Input.MeetingContent");

        var original = await _service.GetOriginalMeetingMinutes(Input.Id);
        if (original == null) return NotFound();

        Input.ProjectId = original.ProjectId;
        var permissionError = await LoadPageStateAsync(original, currentUser);
        if (IsReadOnly)
        {
            Error = permissionError;
            return Page();
        }

        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values
                .SelectMany(value => value.Errors)
                .Select(error => error.ErrorMessage)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct()
                .ToList();
            Error = errors.Count > 0
                ? $"表单验证失败：{string.Join("；", errors)}"
                : "表单验证失败，请检查标题和内容";
            return Page();
        }

        var deletedIds = (DeletedAttachmentIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        var attachmentError = ValidateNewAttachments(deletedIds);
        if (!string.IsNullOrEmpty(attachmentError))
        {
            Error = attachmentError;
            return Page();
        }

        await _service.DeleteAttachments(deletedIds, original, _webHostEnv.WebRootPath, currentUser);
        await _service.AddNewAttachments(NewAttachments, original, _webHostEnv.WebRootPath, currentUser);
        await _service.UpdateMeetingMinutes(original, Input.MeetingTitle, Input.MeetingContent, currentUser, Input.IsDraft);
        original.TranscriptText = string.IsNullOrWhiteSpace(Input.TranscriptText)
            ? null
            : Input.TranscriptText.Trim();
        original.IsDraft = Input.IsDraft;
        original.SubmittedAt = Input.IsDraft ? null : (original.SubmittedAt ?? AppTime.Now);
        await _context.SaveChangesAsync();
        await _eventBus.PublishAsync(
            original.IsDraft ? "meeting-minutes.draft-updated" : "meeting-minutes.updated",
            new
            {
                meetingMinutesId = original.Id,
                projectId = original.ProjectId,
                original.MeetingTitle,
                original.MeetingDate,
                original.IsDraft,
                operatedByUserId = currentUser.Id
            },
            "MeetingMinutes",
            original.Id.ToString());

        if (!original.IsDraft)
        {
            try
            {
                var prepareResult = await _taskSyncService.PrepareAsync(original.Id, currentUser.Id);
                TempData[prepareResult.Success ? "SuccessMessage" : "ErrorMessage"] = prepareResult.Success
                    ? prepareResult.Items.Count > 0
                        ? $"会议纪要修改已保存，并生成 {prepareResult.Items.Count} 项待确认任务；尚未修改项目任务。"
                        : "会议纪要修改已保存，未识别到明确的任务。"
                    : $"会议纪要修改已保存，但任务解析未完成：{prepareResult.ErrorMessage}";
            }
            catch
            {
                TempData["ErrorMessage"] = "会议纪要修改已保存，但任务解析暂时失败，可在详情页重新解析。";
            }
        }
        else
        {
            TempData["SuccessMessage"] = "会议纪要草稿已保存，正式发布后可生成待确认任务。";
        }

        return RedirectToPage("./Details", new { id = original.Id });
    }

    public async Task<IActionResult> OnGetDownloadAsync(int attachmentId)
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return Challenge();

        var attachment = await _context.MeetingAttachments
            .AsNoTracking()
            .Include(item => item.MeetingMinutes)
                .ThenInclude(meeting => meeting.Project)
            .FirstOrDefaultAsync(item => item.Id == attachmentId
                && !item.IsDeleted
                && !item.MeetingMinutes.IsDeleted);
        if (attachment == null) return NotFound();
        if (!await _service.CanAccessMeetingAsync(attachment.MeetingMinutes, currentUser)) return Forbid();

        var meetingRoot = Path.GetFullPath(Path.Combine(_webHostEnv.WebRootPath, "uploads", "meeting"));
        var physicalPath = Path.GetFullPath(Path.Combine(
            _webHostEnv.WebRootPath,
            (attachment.FilePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)));
        if (!physicalPath.StartsWith(meetingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !System.IO.File.Exists(physicalPath))
            return NotFound();

        return PhysicalFile(
            physicalPath,
            string.IsNullOrWhiteSpace(attachment.ContentType) ? "application/octet-stream" : attachment.ContentType,
            attachment.FileName);
    }

    private async Task LoadDependenciesAsync()
    {
        ProjectOptions = await _context.Project.Where(item => !item.IsDeleted && item.Id == Input.ProjectId).OrderBy(item => item.Name).Select(item => new DomainSelectListItem(item.Name, item.Id.ToString())).ToListAsync();
    }

    private async Task<string> LoadPageStateAsync(ToDo.Entities.MeetingMinutes meeting, ApplicationUser currentUser)
    {
        MeetingMinutes = meeting;
        IsReadOnly = !await _service.CheckEditPermission(meeting, currentUser, out var permissionError);
        MeetingCreatedAt = meeting.CreatedAt;
        MeetingLastModifiedAt = meeting.LastModifiedAt;
        ExistingAttachments = meeting.Attachments
            .Where(attachment => !attachment.IsDeleted)
            .Select(ToAttachmentViewModel)
            .ToList();
        await LoadDependenciesAsync();
        return permissionError;
    }

    private string? ValidateNewAttachments(IReadOnlyCollection<int> deletedIds)
    {
        if (NewAttachments.Count > 5) return "最多只能上传5个附件";
        var remainingAttachmentCount = ExistingAttachments.Count(attachment => !deletedIds.Contains(attachment.Id));
        if (remainingAttachmentCount + NewAttachments.Count > 5)
            return "附件总数不能超过5个";

        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".jpg", ".jpeg", ".png", ".txt"
        };

        foreach (var file in NewAttachments)
        {
            if (file.Length > 10 * 1024 * 1024)
                return $"文件「{file.FileName}」超过10MB限制";

            if (!allowedExtensions.Contains(Path.GetExtension(file.FileName)))
                return $"文件「{file.FileName}」格式不支持";
        }

        return null;
    }

    private static AttachmentViewModel ToAttachmentViewModel(MeetingAttachment attachment) => new()
    {
        Id = attachment.Id,
        FileName = attachment.FileName,
        FilePath = attachment.FilePath,
        FileSizeFormatted = FormatFileSize(attachment.FileSize),
        UploadedAt = attachment.UploadedAt,
        IconClass = GetFileIconClass(attachment.FileName)
    };

    private static string FormatFileSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:F1} KB" : $"{bytes / 1024d / 1024d:F1} MB";
    private static string GetFileIconClass(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "fa-file-pdf text-danger",
        ".doc" or ".docx" => "fa-file-word text-primary",
        ".xls" or ".xlsx" => "fa-file-excel text-success",
        ".ppt" or ".pptx" => "fa-file-powerpoint text-warning",
        ".jpg" or ".jpeg" or ".png" => "fa-file-image text-info",
        _ => "fa-file text-dark"
    };
}

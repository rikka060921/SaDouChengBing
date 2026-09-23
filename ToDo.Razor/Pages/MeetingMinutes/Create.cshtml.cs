using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;

namespace ToDo.Razor.Pages.MeetingMinutes;

public class CreateModel : PageModel
{
    private readonly IMeetingMinutesService _service;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _context;
    private readonly IWebHostEnvironment _webHostEnv;
    private readonly MeetingTaskSyncService _taskSyncService;
    private readonly ITencentMeetingLocalCliService _tencentCliService;
    private readonly IMeetingAgendaService _agendaService;
    private readonly IEventBus _eventBus;
    private readonly IAIService _aiService;

    public CreateModel(
        IMeetingMinutesService service,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext context,
        IWebHostEnvironment webHostEnv,
        MeetingTaskSyncService taskSyncService,
        ITencentMeetingLocalCliService tencentCliService,
        IMeetingAgendaService agendaService,
        IEventBus eventBus,
        IAIService aiService)
    {
        _service = service;
        _userManager = userManager;
        _context = context;
        _webHostEnv = webHostEnv;
        _taskSyncService = taskSyncService;
        _tencentCliService = tencentCliService;
        _agendaService = agendaService;
        _eventBus = eventBus;
        _aiService = aiService;
    }

    [BindProperty] public List<IFormFile> Attachments { get; set; } = new();
    [BindProperty] public MeetingMinutesInput MeetingMinutes { get; set; } = new();
    [BindProperty] public string? TencentTranscriptText { get; set; }
    [BindProperty(SupportsGet = true)] public int? ProjectId { get; set; }
    public List<SelectListItem> ProjectOptions { get; set; } = new();
    public List<SelectListItem> MeetingPrepDraftOptions { get; set; } = new();
    public string? DebugInfo { get; set; }

    public class MeetingMinutesInput
    {
        public int Id { get; set; }
        [Required(ErrorMessage = "会议标题不能为空")]
        public string MeetingTitle { get; set; } = string.Empty;
        [Required(ErrorMessage = "请选择会议日期")]
        public DateTime MeetingDate { get; set; }
        [Required(ErrorMessage = "会议内容不能为空")]
        public string MeetingContent { get; set; } = string.Empty;
        public string? TranscriptText { get; set; }

        /// <summary>首项目ID（第一个选中的项目）</summary>
        [Required(ErrorMessage = "请选择项目")]
        public int ProjectId { get; set; }

        /// <summary>关联的所有项目ID（第一个作为首项目写入 ProjectId）</summary>
        public List<int> ProjectIds { get; set; } = new();

        public bool IsDraft { get; set; }
        public int? PrepDraftId { get; set; }
    }

    public class FetchMergeRequest
    {
        public List<string> RecordIds { get; set; } = new();
        public Dictionary<string, string> TitleMap { get; set; } = new();
        public int ProjectId { get; set; }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login", new { returnUrl = "/MeetingMinutes/Create" });
        await LoadProjectOptionsAsync(user);

        // ===== 多项目改造：URL 传 ProjectId 时回显到多选 =====
        if (ProjectId.HasValue && ProjectOptions.Any(option => option.Value == ProjectId.Value.ToString()))
        {
            MeetingMinutes.ProjectIds = new List<int> { ProjectId.Value };
            MeetingMinutes.ProjectId = ProjectId.Value;
        }
        var accessibleProjectIds = (await _service.GetAccessibleProjects(user, activeOnly: true))
            .Select(p => int.Parse(p.Value)).ToList();
        var allDrafts = await _context.MeetingPrepDrafts.AsNoTracking()
            .Where(d => !d.IsDeleted && d.Status == MeetingPrepDraftStatus.Draft)
            .OrderByDescending(d => d.LastModifiedAt)
            .ToListAsync();

        MeetingPrepDraftOptions = allDrafts
            .Where(d =>
            {
                var projIds = System.Text.Json.JsonSerializer.Deserialize<List<int>>(d.SelectedProjectIdsJson ?? "[]");
                return projIds != null && projIds.Intersect(accessibleProjectIds).Any();
            })
            .Select(d => new SelectListItem(
                $"{d.Title}（{d.LastModifiedAt:MM-dd HH:mm}）",
                d.Id.ToString()))
            .ToList();

        MeetingMinutes.MeetingDate = AppTime.Today;
        return Page();
    }

    public async Task<IActionResult> OnGetBriefingAsync(int projectId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var briefing = await _service.GetMeetingBriefingAsync(projectId, user);
        return briefing == null ? NotFound() : new JsonResult(briefing);
    }

    public async Task<IActionResult> OnGetTencentMeetListAsync(DateTime meetDate, int projectId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        if (!await CanCreateMeetingForProjectAsync(user, projectId))
            return new JsonResult(new { success = false, msg = "你没有权限访问该项目的会议数据" })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };

        try
        {
            var list = await _service.QueryTencentDailyMeetListAsync(meetDate);
            return new JsonResult(new { success = true, data = list });
        }
        catch
        {
            return new JsonResult(new { success = false, msg = "查询会议失败，请稍后重试" })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
    }

    public async Task<IActionResult> OnPostFetchMergeTranscriptAsync([FromBody] FetchMergeRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        if (request?.RecordIds == null || !request.RecordIds.Any())
            return new JsonResult(new { success = false, msg = "请至少勾选一场会议" });

        if (!await CanCreateMeetingForProjectAsync(user, request.ProjectId))
            return new JsonResult(new { success = false, msg = "你没有权限访问该项目的会议数据" })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };

        try
        {
            var (mergeText, rawJsonAll, recordJoin) = await _service.BatchMergeTencentMeetTranscriptAsync(
                request.RecordIds,
                request.TitleMap);

            return new JsonResult(new
            {
                success = true,
                content = mergeText,
                rawJson = rawJsonAll,
                recordIds = recordJoin
            });
        }
        catch
        {
            return new JsonResult(new { success = false, msg = "拉取转写失败，请稍后重试" })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login", new { returnUrl = "/MeetingMinutes/Create" });
        if (MeetingMinutes.IsDraft) ModelState.Remove("MeetingMinutes.MeetingContent");

        // ===== 多项目改造：ProjectIds 去重、兜底 =====
        var selectedProjectIds = (MeetingMinutes.ProjectIds ?? new List<int>())
            .Where(x => x > 0)
            .Distinct()
            .ToList();

        if (selectedProjectIds.Count == 0 && MeetingMinutes.ProjectId > 0)
            selectedProjectIds.Add(MeetingMinutes.ProjectId);

        // 首项目必须是 ProjectId，如果不一致则插入到第一个
        if (MeetingMinutes.ProjectId > 0 && !selectedProjectIds.Contains(MeetingMinutes.ProjectId))
            selectedProjectIds.Insert(0, MeetingMinutes.ProjectId);

        if (selectedProjectIds.Count > 0)
            MeetingMinutes.ProjectId = selectedProjectIds[0];

        // 把 ProjectIds 补回去，方便回显
        MeetingMinutes.ProjectIds = selectedProjectIds;

        if (selectedProjectIds.Count == 0)
        {
            ModelState.AddModelError("MeetingMinutes.ProjectIds", "请至少选择一个项目");
        }

        if (!ModelState.IsValid)
        {
            await LoadProjectOptionsAsync(user);
            return Page();
        }

        // ===== 多项目改造：遍历所有选中项目，全部要有权限 =====
        var projects = await _context.Project
            .Where(p => selectedProjectIds.Contains(p.Id) && !p.IsDeleted)
            .ToListAsync();
        if (projects.Count != selectedProjectIds.Count)
        {
            ModelState.AddModelError("MeetingMinutes.ProjectIds", "部分项目不存在或已被删除");
            await LoadProjectOptionsAsync(user);
            return Page();
        }

        foreach (var proj in projects)
        {
            var perm = await _service.CheckCreatePermission(user, proj);
            if (!perm.HasPermission)
            {
                ModelState.AddModelError("MeetingMinutes.ProjectIds", perm.ErrorMessage);
                await LoadProjectOptionsAsync(user);
                return Page();
            }
        }

        var project = projects.First(p => p.Id == MeetingMinutes.ProjectId);

        if (MeetingMinutes.MeetingDate > AppTime.Today)
        {
            ModelState.AddModelError("MeetingMinutes.MeetingDate", "会议日期不能超过今天");
            await LoadProjectOptionsAsync(user);
            return Page();
        }
        if (Attachments.Count > 5)
        {
            ModelState.AddModelError(string.Empty, "最多只能上传5个文件");
            await LoadProjectOptionsAsync(user);
            return Page();
        }

        foreach (var file in Attachments)
        {
            if (file.Length > 10 * 1024 * 1024)
            {
                ModelState.AddModelError(string.Empty, $"文件「{file.FileName}」超过10MB限制");
                await LoadProjectOptionsAsync(user);
                return Page();
            }
        }

        if (string.IsNullOrWhiteSpace(MeetingMinutes.MeetingTitle))
            MeetingMinutes.MeetingTitle = $"{user.UserName}_{MeetingMinutes.MeetingDate:yyyyMMdd}_{project.Name}";
        if (!MeetingMinutes.IsDraft && string.IsNullOrWhiteSpace(MeetingMinutes.MeetingContent))
        {
            ModelState.AddModelError("MeetingMinutes.MeetingContent", "会议内容不能为空");
            await LoadProjectOptionsAsync(user);
            return Page();
        }

        var sourceType = MeetingSourceType.Manual;
        string rawTranscriptJson = string.Empty;
        string cleanContent = string.Empty;
        string tencentRecordIds = string.Empty;

        var sourceKind = Request.Form["SourceKind"].ToString();
        if (sourceKind == "1")
        {
            sourceType = MeetingSourceType.TencentMeeting;
            rawTranscriptJson = Request.Form["RawTranscriptJson"].ToString() ?? string.Empty;
            cleanContent = Request.Form["CleanContent"].ToString() ?? string.Empty;
            tencentRecordIds = Request.Form["SelectedRecordIds"].ToString() ?? string.Empty;
        }
        else if (sourceKind == "2")
        {
            sourceType = MeetingSourceType.PastedTranscript;
            cleanContent = Request.Form["CleanContent"].ToString() ?? string.Empty;
            // rawTranscriptJson 和 tencentRecordIds 保持为空
        }

        var entity = new ToDo.Entities.MeetingMinutes
        {
            MeetingTitle = MeetingMinutes.MeetingTitle.Trim(),
            MeetingDate = MeetingMinutes.MeetingDate,
            MeetingContent = MeetingMinutes.MeetingContent ?? string.Empty,
            TranscriptText = !string.IsNullOrWhiteSpace(MeetingMinutes.TranscriptText)
                ? MeetingMinutes.TranscriptText.Trim()
                : !string.IsNullOrWhiteSpace(TencentTranscriptText)
                    ? TencentTranscriptText.Trim()
                    : (!string.IsNullOrWhiteSpace(cleanContent) ? cleanContent : null),
            ProjectId = MeetingMinutes.ProjectId,   // 首项目
            PrepDraftId = MeetingMinutes.PrepDraftId,
            CreatorId = user.Id,
            IsDraft = MeetingMinutes.IsDraft,
            SubmittedAt = null,
            SourceType = sourceType,
            RawTranscriptJson = rawTranscriptJson,
            CleanContent = cleanContent,
            TencentRecordFileIds = tencentRecordIds
        };

        // Save the meeting and all project links in the service's single transaction.
        foreach (var pid in selectedProjectIds)
            entity.MeetingProjects.Add(new MeetingMinutesProject { ProjectId = pid, IsPrimary = pid == entity.ProjectId });
        await _service.SaveMeetingMinutes(entity, Attachments, _webHostEnv.WebRootPath, user);

        await _eventBus.PublishAsync(
            entity.IsDraft ? "meeting-minutes.draft-created" : "meeting-minutes.published",
            new
            {
                meetingMinutesId = entity.Id,
                projectId = entity.ProjectId,
                projectIds = selectedProjectIds,
                entity.MeetingTitle,
                entity.MeetingDate,
                entity.IsDraft,
                creatorId = user.Id
            },
            "MeetingMinutes",
            entity.Id.ToString());

        if (entity.IsDraft)
        {
            TempData["SuccessMessage"] = "会议草稿已保存；正式提交后才会解析待确认任务。";
        }
        else
        {
            try
            {
                var prepareResult = await _taskSyncService.PrepareAsync(entity.Id, user.Id);
                TempData[prepareResult.Success ? "SuccessMessage" : "ErrorMessage"] = prepareResult.Success
                    ? prepareResult.Items.Count > 0
                        ? $"会议纪要已保存，并生成 {prepareResult.Items.Count} 项待确认任务；尚未修改项目任务。"
                        : "会议纪要已保存，未识别到明确的任务。"
                    : $"会议纪要已保存，但任务解析未完成：{prepareResult.ErrorMessage}";
            }
            catch
            {
                TempData["ErrorMessage"] = "会议纪要已保存，但任务解析暂时失败，可在详情页重新解析。";
            }
        }

        return RedirectToPage("./Details", new { id = entity.Id });
    }

    private async Task LoadProjectOptionsAsync(ApplicationUser user)
    {
        ProjectOptions = (await _service.GetAccessibleProjects(user, activeOnly: true))
            .Select(item => new SelectListItem(item.Text, item.Value)).ToList();
    }

    private async Task<bool> CanCreateMeetingForProjectAsync(ApplicationUser user, int projectId)
    {
        if (projectId <= 0) return false;
        var project = await _context.Project
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == projectId && !item.IsDeleted);
        return project != null && (await _service.CheckCreatePermission(user, project)).HasPermission;
    }

    public class GenerateMeetingRequest
    {
        public string Transcript { get; set; } = string.Empty;
        public int ProjectId { get; set; }
    }

    public async Task<IActionResult> OnPostGenerateMeetingFromTranscriptAsync([FromBody] GenerateMeetingRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        if (request == null)
            return BadRequest(new { success = false, message = "请求内容不能为空" });

        if (string.IsNullOrWhiteSpace(request.Transcript))
            return new JsonResult(new { success = false, message = "转写文本不能为空" });

        if (request.ProjectId <= 0)
            return new JsonResult(new { success = false, message = "请选择有效的项目" });

        var project = await _context.Project
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == request.ProjectId && !item.IsDeleted);
        if (project == null) return NotFound(new { success = false, message = "项目不存在或已删除" });

        var permission = await _service.CheckCreatePermission(user, project);
        if (!permission.HasPermission)
        {
            return new JsonResult(new { success = false, message = permission.ErrorMessage })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }

        try
        {
            var template = GetMeetingMinutesTemplate(request.ProjectId);

            var result = await _aiService.GenerateMeetingMinutesAsync(
                request.Transcript,
                template,
                request.ProjectId,
                HttpContext.RequestAborted);

            if (!result.Success)
                return new JsonResult(new { success = false, message = result.ErrorMessage });

            return new JsonResult(new
            {
                success = true,
                content = result.Content,
                title = result.Title
            });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch
        {
            return new JsonResult(new { success = false, message = "生成失败，请稍后重试" })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    private string GetMeetingMinutesTemplate(int projectId)
    {
        return GetDefaultTemplate();
    }

    private static string GetDefaultTemplate()
    {
        return @"# 会议纪要

**会议名称：** XXX的个人会议室 19:00~21:00 — 会议主题

| 项目 | 内容 |
|------|------|
| 参会人 | 赵冬、赵珂薇、乔宽、贺楚 |
| 会议核心主题 | 本次会议的核心议题概括（一句话） |

---

## 会议整体概述

本次会议主要围绕XXX进行梳理与确认。会议从XXX切入，明确了XXX。随后重点讨论了XXX。此外，会议还对XXX等问题进行了讨论。整体会议以XXX为主，明确了下游需要调整的方向，但部分页面交互细节待后续进一步确认。

---

## 讨论要点

### 话题一：XXX

**讨论内容：**
1. XX提出XXX
2. XX认为XXX
3. XX建议XXX
4. 最终确认XXX

**涉及人员：** XXX、XXX

### 话题二：XXX

**讨论内容：**
1. XX提出XXX
2. XX介绍当前方案XXX
3. XX进一步提出XXX

**涉及人员：** XXX、XXX

---

## 会上遗留疑问 & 有待进一步斟酌内容

1. XXX，待XXX后根据实际使用体验再调整
2. XXX的具体格式模板尚未确定，需XXX和XXX会后查找确认
3. XXX的具体改动方案待后续确认

---

## 会议记录的待办

| 序号 | 待办内容 | 负责人 | 配合人 | 时间节点 | 备注 |
|------|----------|--------|--------|----------|------|
| 1 | XXX | @XXX | @XXX | XXX | XXX |
| 2 | XXX | @XXX | 【未明确】 | 【未明确】 | XXX |
| 3 | XXX | @XXX | @XXX | 【未明确】 | 便于团队统一理解和对齐 |

---

## 📎 其他补充

- XXX
- XXX

---

## ⚠️ 需关注事项

- XXX
- XXX
- XXX
- XXX";
    }
}

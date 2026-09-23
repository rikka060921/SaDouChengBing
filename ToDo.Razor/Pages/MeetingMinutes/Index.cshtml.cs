using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;

namespace ToDo.Razor.Pages.MeetingMinutes;

public class IndexModel : PageModel
{
    private readonly IMeetingMinutesService _service;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _context;
    private readonly IAIService _aiService;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(IMeetingMinutesService service, UserManager<ApplicationUser> userManager, ApplicationDbContext context, IAIService aiService, ILogger<IndexModel> logger)
    {
        _service = service;
        _userManager = userManager;
        _context = context;
        _aiService = aiService;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)] public string? FilterKeyword { get; set; }
    [BindProperty(SupportsGet = true)] public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public int TotalCount { get; set; }
    public int TotalPages => (int)Math.Ceiling(decimal.Divide(TotalCount, PageSize));
    public Dictionary<int, bool> CanEditDictionary { get; set; } = new();
    public Dictionary<int, bool> CanDeleteDictionary { get; set; } = new();
    public bool CanCreate { get; set; }
    public List<ToDo.Entities.MeetingMinutes> MeetingMinutesList { get; set; } = new();
    public List<Project> Projects { get; set; } = new();
    public List<ApplicationUser> Creators { get; set; } = new();
    [BindProperty(SupportsGet = true)] public int? FilterProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterTitle { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterCreator { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? FilterStartDate { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? FilterEndDate { get; set; }
    [BindProperty] public int? AiProcessId { get; set; }
    [BindProperty] public string? AiProcessType { get; set; }
    public string? AiProcessResult { get; set; }
    public string? AiProcessError { get; set; }
    public string? LoadError { get; set; }
    public ToDo.Entities.MeetingMinutes? SelectedMeetingMinute { get; set; }

    public async Task OnGetAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load meeting minutes list");
            LoadError = "会议纪要暂时无法加载，请稍后重试";
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();
        var result = await _service.DeleteMeetingMinutes(id, user);
        TempData[result.Success ? "SuccessMessage" : "ErrorMessage"] = result.Message;
        return RedirectToPage("./Index");
    }

    public async Task<IActionResult> OnPostProcessWithAIAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) { AiProcessError = "请先登录系统"; await OnGetAsync(); return Page(); }
        if (!AiProcessId.HasValue || string.IsNullOrWhiteSpace(AiProcessType)) { AiProcessError = "请选择会议纪要和AI处理类型"; await OnGetAsync(); return Page(); }
        SelectedMeetingMinute = await _context.MeetingMinutes.FirstOrDefaultAsync(item => item.Id == AiProcessId.Value && !item.IsDeleted);
        if (SelectedMeetingMinute == null) { AiProcessError = "会议纪要不存在或已被删除"; await OnGetAsync(); return Page(); }
        if (!await _service.CanAccessMeetingAsync(SelectedMeetingMinute, user)) { AiProcessError = "你没有权限处理该会议纪要"; await OnGetAsync(); return Page(); }
        try
        {
            var prompt = AiProcessType switch
            {
                "summary" => $"请总结以下会议纪要的核心内容，控制在200字以内：\n{SelectedMeetingMinute.MeetingContent}",
                "todo" => $"请提取以下会议纪要中的待办事项，每行一个：\n{SelectedMeetingMinute.MeetingContent}",
                "optimize" => $"请优化以下会议纪要，保持原意并提升结构化：\n{SelectedMeetingMinute.MeetingContent}",
                _ => throw new ArgumentException("不支持的AI处理类型")
            };
            AiProcessResult = await _aiService.GetChatCompletionAsync(prompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process meeting {MeetingId} with AI operation {Operation}", AiProcessId, AiProcessType);
            AiProcessError = "AI处理失败，请稍后重试";
        }
        await OnGetAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostApplyAiResultAsync(int id, string aiContent)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();
        if (string.IsNullOrWhiteSpace(aiContent) || aiContent.Length > 100_000)
        {
            TempData["ErrorMessage"] = "AI 结果为空或内容过长，未替换会议纪要";
            return RedirectToPage("./Index");
        }

        var meeting = await _context.MeetingMinutes
            .Include(item => item.Project)
            .FirstOrDefaultAsync(item => item.Id == id && !item.IsDeleted);
        if (meeting == null) return NotFound();
        try
        {
            await _service.UpdateMeetingMinutes(meeting, meeting.MeetingTitle, aiContent, user, meeting.IsDraft);
            TempData["SuccessMessage"] = "AI 处理结果已替换原纪要，并生成新的历史版本";
            return RedirectToPage("./Edit", new { id });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply AI result to meeting {MeetingId}", id);
            TempData["ErrorMessage"] = "替换失败，原会议纪要未被修改";
            return RedirectToPage("./Index", new { filterProjectId = meeting.ProjectId });
        }
    }

    private async Task LoadAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return;
        CanCreate = true;
        var accessibleProjects = await _service.GetAccessibleProjects(user);
        var accessibleProjectIds = accessibleProjects
            .Select(item => int.TryParse(item.Value, out var projectId) ? (int?)projectId : null)
            .Where(projectId => projectId.HasValue)
            .Select(projectId => projectId!.Value)
            .ToList();
        Projects = await _context.Project
            .Where(item => accessibleProjectIds.Contains(item.Id) && !item.IsDeleted)
            .OrderBy(item => item.Name)
            .ToListAsync();
        var accessibleCreatorIds = await _context.MeetingMinutes.AsNoTracking()
            .Where(meeting => accessibleProjectIds.Contains(meeting.ProjectId) && !meeting.IsDeleted)
            .Select(meeting => meeting.CreatorId)
            .Distinct()
            .ToListAsync();
        Creators = await _context.Users.AsNoTracking()
            .Where(creator => accessibleCreatorIds.Contains(creator.Id) && !creator.IsDeleted)
            .OrderBy(creator => creator.RealName)
            .ToListAsync();
        CurrentPage = Math.Max(CurrentPage, 1);
        var result = await _service.GetFilteredMeetingMinutes(
            FilterProjectId, FilterTitle ?? string.Empty, FilterCreator ?? string.Empty,
            FilterKeyword ?? string.Empty, FilterStartDate, FilterEndDate,
            CurrentPage, PageSize, user);
        MeetingMinutesList = result.Items;
        TotalCount = result.TotalCount;
        CanEditDictionary = await _service.GetCanEditPermissions(MeetingMinutesList, user);
        CanDeleteDictionary = await _service.GetCanDeletePermissions(MeetingMinutesList, user);
    }
}

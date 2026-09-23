using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.MeetingMinutes;

[Authorize]
public sealed class SupervisionModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly MeetingActionSupervisionService _supervision;

    public SupervisionModel(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        MeetingActionSupervisionService supervision)
    {
        _context = context;
        _userManager = userManager;
        _supervision = supervision;
    }

    [BindProperty(SupportsGet = true)] public int? ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public MeetingActionSupervisionStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public int? EscalationLevel { get; set; }
    [BindProperty(SupportsGet = true)] public bool IncludeCompleted { get; set; }
    public List<Project> Projects { get; private set; } = [];
    public List<MeetingActionItem> Items { get; private set; } = [];
    public int AttentionCount { get; private set; }
    public int OverdueCount { get; private set; }
    public int BlockedCount { get; private set; }
    public int UnlinkedCount { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostRunAsync(int? projectId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        var managed = await ManagedProjectIdsAsync(user);
        if (projectId.HasValue && !managed.Contains(projectId.Value)) return Forbid();
        var projectIds = projectId.HasValue ? [projectId.Value] : managed;
        var meetingIds = await _context.MeetingMinutes.AsNoTracking()
            .Where(item => projectIds.Contains(item.ProjectId) && !item.IsDeleted)
            .Select(item => item.Id).ToListAsync();
        var evaluated = 0;
        foreach (var meetingId in meetingIds)
        {
            var result = await _supervision.RunAsync(AppTime.Now, meetingId, HttpContext.RequestAborted);
            evaluated += result.Evaluated;
        }
        TempData["SuccessMessage"] = $"督办检查完成，共检查 {evaluated} 个会议行动项";
        return RedirectToPage(new { ProjectId = projectId, Status, EscalationLevel, IncludeCompleted });
    }

    public async Task<IActionResult> OnPostAcknowledgeAsync(
        int id,
        DateTime? nextCheckpointAt,
        string? resolutionNote)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        var item = await _context.MeetingActionItems
            .Include(action => action.MeetingMinutes)!.ThenInclude(meeting => meeting!.Project)
            .Include(action => action.MatchedTask)
            .FirstOrDefaultAsync(action => action.Id == id);
        if (item?.MeetingMinutes == null) return NotFound();
        if (!await CanHandleAsync(item, user)) return Forbid();
        var now = AppTime.Now;
        var checkpoint = nextCheckpointAt.HasValue && nextCheckpointAt > now
            ? nextCheckpointAt.Value
            : now.AddHours(24);
        item.SupervisionAcknowledgedByUserId = user.Id;
        item.SupervisionAcknowledgedAt = now;
        item.NextCheckpointAt = checkpoint;
        item.SnoozedUntil = checkpoint;
        item.SupervisionResolutionNote = Truncate(resolutionNote, 1000);
        _context.MeetingActionSupervisionEvents.Add(new MeetingActionSupervisionEvent
        {
            ActionItemId = item.Id,
            MeetingMinutesId = item.MeetingMinutesId,
            ProjectId = item.MeetingMinutes.ProjectId,
            TaskId = item.MatchedTaskId,
            EventType = MeetingActionSupervisionEventType.Acknowledged,
            Status = item.SupervisionStatus,
            EscalationLevel = item.EscalationLevel,
            EventKey = $"meeting-action:{item.Id}:ack:{now.Ticks}:{user.Id}",
            RecipientIdsJson = JsonSerializer.Serialize(new[] { user.Id }),
            Message = $"{user.RealName ?? user.UserName} 已接手处理；下一检查时间 {checkpoint:yyyy-MM-dd HH:mm}。{item.SupervisionResolutionNote}",
            CreatedAt = now
        });
        await _context.SaveChangesAsync();
        TempData["SuccessMessage"] = "已记录接手人和下一检查时间，到期前不再重复提醒";
        return RedirectToPage(new { ProjectId, Status, EscalationLevel, IncludeCompleted });
    }

    private async Task LoadAsync(ApplicationUser user)
    {
        var accessible = await AccessibleProjectIdsAsync(user);
        Projects = await _context.Project.AsNoTracking()
            .Where(item => accessible.Contains(item.Id) && !item.IsDeleted)
            .OrderBy(item => item.Name).ToListAsync();
        if (ProjectId.HasValue && !accessible.Contains(ProjectId.Value)) ProjectId = null;
        var query = _context.MeetingActionItems.AsNoTracking()
            .Include(item => item.MeetingMinutes)!.ThenInclude(meeting => meeting!.Project)
            .Include(item => item.MatchedTask)!.ThenInclude(task => task!.Assignee)
            .Include(item => item.Assignee)
            .Where(item => item.IsConfirmed && item.MeetingMinutes != null
                && !item.MeetingMinutes.IsDeleted
                && accessible.Contains(item.MeetingMinutes.ProjectId));
        if (ProjectId.HasValue) query = query.Where(item => item.MeetingMinutes!.ProjectId == ProjectId.Value);
        if (Status.HasValue) query = query.Where(item => item.SupervisionStatus == Status.Value);
        if (EscalationLevel.HasValue) query = query.Where(item => item.EscalationLevel == EscalationLevel.Value);
        if (!IncludeCompleted) query = query.Where(item => item.SupervisionStatus != MeetingActionSupervisionStatus.Completed
            && item.SupervisionStatus != MeetingActionSupervisionStatus.Cancelled);
        Items = await query.OrderByDescending(item => item.EscalationLevel)
            .ThenBy(item => item.SnoozedUntil.HasValue && item.SnoozedUntil > AppTime.Now)
            .ThenBy(item => item.Deadline)
            .Take(500).ToListAsync();
        AttentionCount = Items.Count(item => item.SupervisionStatus is MeetingActionSupervisionStatus.PendingLink
            or MeetingActionSupervisionStatus.NeedsDefinition or MeetingActionSupervisionStatus.DueSoon
            or MeetingActionSupervisionStatus.Overdue or MeetingActionSupervisionStatus.Blocked);
        OverdueCount = Items.Count(item => item.SupervisionStatus == MeetingActionSupervisionStatus.Overdue);
        BlockedCount = Items.Count(item => item.SupervisionStatus == MeetingActionSupervisionStatus.Blocked);
        UnlinkedCount = Items.Count(item => item.SupervisionStatus == MeetingActionSupervisionStatus.PendingLink);
    }

    private async Task<List<int>> AccessibleProjectIdsAsync(ApplicationUser user)
    {
        if (user.Role == UserRole.systemAdmin)
            return await _context.Project.AsNoTracking().Where(item => !item.IsDeleted).Select(item => item.Id).ToListAsync();
        return await _context.Project.AsNoTracking().Where(item => !item.IsDeleted
            && (item.LeaderUserId == user.Id || _context.ProjectUsers.Any(member => member.ProjectId == item.Id && member.UserId == user.Id)))
            .Select(item => item.Id).ToListAsync();
    }

    private async Task<List<int>> ManagedProjectIdsAsync(ApplicationUser user)
    {
        if (user.Role == UserRole.systemAdmin)
            return await _context.Project.AsNoTracking().Where(item => !item.IsDeleted).Select(item => item.Id).ToListAsync();
        return await _context.Project.AsNoTracking().Where(item => !item.IsDeleted
            && (item.LeaderUserId == user.Id || _context.ProjectUsers.Any(member => member.ProjectId == item.Id && member.UserId == user.Id && member.ProjectRole == (int)ProjectRole.Admin)))
            .Select(item => item.Id).ToListAsync();
    }

    private async Task<bool> CanHandleAsync(MeetingActionItem item, ApplicationUser user)
        => user.Role == UserRole.systemAdmin
            || item.AssigneeId == user.Id
            || item.MatchedTask?.AssigneeId == user.Id
            || item.MeetingMinutes?.LeaderFallback(user.Id) == true
            || await _context.ProjectUsers.AsNoTracking().AnyAsync(member => member.ProjectId == item.MeetingMinutes!.ProjectId
                && member.UserId == user.Id && member.ProjectRole == (int)ProjectRole.Admin);

    public static string StatusLabel(MeetingActionSupervisionStatus status) => status switch
    {
        MeetingActionSupervisionStatus.PendingLink => "待关联",
        MeetingActionSupervisionStatus.NeedsDefinition => "待补条件",
        MeetingActionSupervisionStatus.OnTrack => "正常推进",
        MeetingActionSupervisionStatus.DueSoon => "即将到期",
        MeetingActionSupervisionStatus.Overdue => "已逾期",
        MeetingActionSupervisionStatus.Blocked => "受阻",
        MeetingActionSupervisionStatus.Completed => "已完成",
        MeetingActionSupervisionStatus.Cancelled => "已取消",
        _ => status.ToString()
    };

    private static string Truncate(string? value, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}

internal static class MeetingSupervisionProjectExtensions
{
    public static bool LeaderFallback(this ToDo.Entities.MeetingMinutes meeting, int userId)
        => meeting.Project?.LeaderUserId == userId;
}

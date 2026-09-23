using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Projects;

[Authorize]
public class MeetingPreparationModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<MeetingPreparationModel> _logger;

    public MeetingPreparationModel(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IAntiforgery antiforgery,
        ILogger<MeetingPreparationModel> logger)
    {
        _context = context;
        _userManager = userManager;
        _antiforgery = antiforgery;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)]
    public int ProjectId { get; set; }

    /// <summary>编辑已有草稿时传入的草稿ID（0 表示新建）</summary>
    [BindProperty(SupportsGet = true)]
    public int DraftId { get; set; }

    public int? CurrentDraftId { get; set; }
    public string DraftTitle { get; set; } = string.Empty;
    public List<ProjectOption> AvailableProjects { get; set; } = new();
    public List<int> SelectedProjectIds { get; set; } = new();
    public List<int> SelectedTaskIds { get; set; } = new();
    public List<MeetingPrepTaskRow> CurrentTasks { get; set; } = new();
    /// <summary>上次会议至今已完成的任务，按项目分组（projectId → 列表）</summary>
    public Dictionary<int, List<MeetingPrepTaskRow>> RecentlyCompletedByProject { get; set; } = new();
    public bool IsReadOnly { get; set; }
    public string AntiforgeryToken { get; set; } = string.Empty;
    public string? LoadError { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        try
        {
            AntiforgeryToken = _antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;

            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Forbid();

            // 加载可访问项目列表（口径与任务列表页一致：负责人或项目成员）
            var accessibleIds = await GetAccessibleProjectIdsAsync(user);
            AvailableProjects = await _context.Project.AsNoTracking()
                .Where(p => !p.IsDeleted && p.Status == ProjectStatus.Active && accessibleIds.Contains(p.Id))
                .OrderBy(p => p.Name)
                .Select(p => new ProjectOption { Id = p.Id, Name = p.Name })
                .ToListAsync();

            // 编辑已有草稿
            if (DraftId > 0)
            {
                var draft = await _context.MeetingPrepDrafts
                    .FirstOrDefaultAsync(d => d.Id == DraftId && !d.IsDeleted);
                if (draft == null) return NotFound();

                // 仅创建者或系统管理员可编辑
                var canEdit = draft.CreatorId == user.Id || user.Role == UserRole.systemAdmin;
                if (!canEdit) return Forbid();

                CurrentDraftId = draft.Id;
                DraftTitle = draft.Title;
                IsReadOnly = draft.Status == MeetingPrepDraftStatus.Finalized;

                SelectedProjectIds = JsonSerializer.Deserialize<List<int>>(draft.SelectedProjectIdsJson) ?? new();
                SelectedTaskIds = JsonSerializer.Deserialize<List<int>>(draft.SelectedTaskIdsJson) ?? new();
                IsReadOnly |= await _context.Project.AnyAsync(p => SelectedProjectIds.Contains(p.Id) && p.Status == ProjectStatus.Archived);
                if (IsReadOnly)
                    AvailableProjects = await _context.Project.AsNoTracking()
                        .Where(p => accessibleIds.Contains(p.Id) && SelectedProjectIds.Contains(p.Id) && !p.IsDeleted)
                        .Select(p => new ProjectOption { Id = p.Id, Name = p.Name + (p.Status == ProjectStatus.Archived ? "（已归档）" : "") }).ToListAsync();
            }
            else
            {
                // 新建：默认勾选当前项目
                if (ProjectId > 0 && AvailableProjects.Any(p => p.Id == ProjectId))
                {
                    SelectedProjectIds = new List<int> { ProjectId };
                    var projectName = AvailableProjects.First(p => p.Id == ProjectId).Name;
                    DraftTitle = $"会前准备_{AppTime.Today:yyyyMMdd}_{projectName}";
                }
            }

            // 加载过滤任务
            CurrentTasks = await LoadFilteredTasksAsync(SelectedProjectIds);
            RecentlyCompletedByProject = await LoadRecentlyCompletedTasksAsync(SelectedProjectIds);

            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load meeting preparation, ProjectId={ProjectId}, DraftId={DraftId}", ProjectId, DraftId);
            LoadError = "会前准备数据暂时无法加载，请稍后重试";
            AntiforgeryToken = _antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;
            return Page();
        }
    }

    /// <summary>AJAX：按勾选项目实时刷新任务列表</summary>
    public async Task<JsonResult> OnGetFilteredTasksAsync([FromQuery] int[] projectIds)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return new JsonResult(new { success = false, message = "未登录" });

        // 校验请求的项目都在用户可访问范围内
        var accessibleIds = await GetAccessibleProjectIdsAsync(user);
        var validProjectIds = projectIds?.Where(id => accessibleIds.Contains(id)).ToList() ?? new List<int>();

        var tasks = await LoadFilteredTasksAsync(validProjectIds);
        var completedByProject = await LoadRecentlyCompletedTasksAsync(validProjectIds);
        return new JsonResult(new { success = true, tasks, recentlyCompletedByProject = completedByProject });
    }

    /// <summary>保存草稿（新建或更新）</summary>
    public async Task<JsonResult> OnPostSaveAsync()
    {
        try
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return new JsonResult(new { success = false, message = "未登录" });

            using var reader = new System.IO.StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            // Web 默认：属性名大小写不敏感（前端发小写 title/projectIds，C# 属性为 Title/ProjectIds）
            var data = JsonSerializer.Deserialize<SaveRequestBody>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

            if (string.IsNullOrWhiteSpace(data.Title))
                return new JsonResult(new { success = false, message = "草稿标题不能为空" });
            if (data.ProjectIds == null || data.ProjectIds.Length == 0)
                return new JsonResult(new { success = false, message = "请至少选择一个项目" });

            // 校验项目权限
            var accessibleIds = await GetAccessibleProjectIdsAsync(user);
            var validProjectIds = data.ProjectIds.Where(id => accessibleIds.Contains(id)).ToList();
            if (validProjectIds.Count == 0)
                return new JsonResult(new { success = false, message = "所选项目无访问权限" });

            // 查任务快照：只取选中且仍符合条件的任务
            var validTaskIds = data.TaskIds?.ToList() ?? new List<int>();
            var now = AppTime.Now;
            var tasks = await _context.ToDoTasks
                .Where(t => validTaskIds.Contains(t.Id)
                    && validProjectIds.Contains(t.ProjectId)
                    && !t.IsDeleted
                    && (t.Status == ToDo.Entities.TaskStatus.NotStarted
                        || t.Status == ToDo.Entities.TaskStatus.InProgress
                        || t.Status == ToDo.Entities.TaskStatus.PendingConfirmation))
                .Select(t => new MeetingPrepTaskRow
                {
                    Id = t.Id,
                    Title = t.Title,
                    ProjectId = t.ProjectId,
                    ProjectName = t.Project != null ? t.Project.Name : "",
                    Status = t.Status,
                    Priority = t.Priority,
                    DeadlineAt = t.EndTime,
                    AssigneeName = t.AssigneeType == TaskAssigneeType.Human
                        ? (t.Assignee != null ? (t.Assignee.RealName ?? t.Assignee.UserName) : "未分配")
                        : (t.AgentName ?? "数字员工"),
                    IsOverdue = t.EndTime.HasValue && t.EndTime < now
                })
                .ToListAsync();

            var taskSnapshot = tasks.Select(t => new
            {
                id = t.Id,
                title = t.Title,
                status = t.Status.ToString(),
                projectName = t.ProjectName,
                assigneeName = t.AssigneeName,
                deadline = t.DeadlineAt?.ToString("yyyy-MM-dd"),
                isOverdue = t.IsOverdue
            }).ToList();

            if (data.DraftId > 0)
            {
                // 更新已有草稿
                var draft = await _context.MeetingPrepDrafts
                    .FirstOrDefaultAsync(d => d.Id == data.DraftId && !d.IsDeleted);
                if (draft == null)
                    return new JsonResult(new { success = false, message = "草稿不存在" });
                if (draft.CreatorId != user.Id && user.Role != UserRole.systemAdmin)
                    return new JsonResult(new { success = false, message = "无编辑权限" });
                if (draft.Status == MeetingPrepDraftStatus.Finalized)
                    return new JsonResult(new { success = false, message = "草稿已定稿，不可修改" });

                draft.Title = data.Title.Trim();
                draft.SelectedProjectIdsJson = JsonSerializer.Serialize(validProjectIds);
                draft.SelectedTaskIdsJson = JsonSerializer.Serialize(tasks.Select(t => t.Id).ToList());
                draft.TaskSnapshotJson = JsonSerializer.Serialize(taskSnapshot);
                draft.LastModifiedAt = AppTime.Now;
                await _context.SaveChangesAsync();
                return new JsonResult(new { success = true, draftId = draft.Id });
            }
            else
            {
                // 新建草稿
                var draft = new MeetingPrepDraft
                {
                    Title = data.Title.Trim(),
                    CreatorId = user.Id,
                    Status = MeetingPrepDraftStatus.Draft,
                    SelectedProjectIdsJson = JsonSerializer.Serialize(validProjectIds),
                    SelectedTaskIdsJson = JsonSerializer.Serialize(tasks.Select(t => t.Id).ToList()),
                    TaskSnapshotJson = JsonSerializer.Serialize(taskSnapshot),
                    CreatedAt = AppTime.Now,
                    LastModifiedAt = AppTime.Now
                };
                _context.MeetingPrepDrafts.Add(draft);
                await _context.SaveChangesAsync();
                return new JsonResult(new { success = true, draftId = draft.Id });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save meeting prep draft");
            return new JsonResult(new { success = false, message = "保存失败，请稍后重试" });
        }
    }

    /// <summary>
    /// 获取用户可访问的项目ID集合。口径与任务列表页一致：
    /// 系统管理员可访问全部项目；普通用户为其负责或加入的项目（不额外限制加密项目）。
    /// </summary>
    private async Task<HashSet<int>> GetAccessibleProjectIdsAsync(ApplicationUser user)
    {
        if (user.Role == UserRole.systemAdmin)
        {
            return (await _context.Project.AsNoTracking()
                .Where(p => !p.IsDeleted)
                .Select(p => p.Id)
                .ToListAsync()).ToHashSet();
        }

        var asLeader = await _context.Project.AsNoTracking()
            .Where(p => !p.IsDeleted && p.LeaderUserId == user.Id)
            .Select(p => p.Id)
            .ToListAsync();
        var asMember = await _context.ProjectUsers.AsNoTracking()
            .Where(pu => pu.UserId == user.Id && !pu.Project.IsDeleted)
            .Select(pu => pu.ProjectId)
            .ToListAsync();
        return asLeader.Concat(asMember).ToHashSet();
    }

    /// <summary>加载过滤任务：仅展示未开始、进行中、待审核的任务；排序：逾期→临近截止3天→普通→待审核，组内按截止日期升序</summary>
    private async Task<List<MeetingPrepTaskRow>> LoadFilteredTasksAsync(IEnumerable<int> projectIds)
    {
        var ids = projectIds?.ToList() ?? new List<int>();
        if (ids.Count == 0) return new List<MeetingPrepTaskRow>();

        var now = AppTime.Now;
        var threeDaysFromNow = now.AddDays(3);
        var rows = await _context.ToDoTasks
            .Where(t => ids.Contains(t.ProjectId) && !t.IsDeleted
                && (t.Status == ToDo.Entities.TaskStatus.NotStarted
                    || t.Status == ToDo.Entities.TaskStatus.InProgress
                    || t.Status == ToDo.Entities.TaskStatus.PendingConfirmation))
            .Select(t => new MeetingPrepTaskRow
            {
                Id = t.Id,
                Title = t.Title,
                ProjectId = t.ProjectId,
                ProjectName = t.Project != null ? t.Project.Name : "",
                Status = t.Status,
                Priority = t.Priority,
                DeadlineAt = t.EndTime,
                AssigneeName = t.AssigneeType == TaskAssigneeType.Human
                    ? (t.Assignee != null ? (t.Assignee.RealName ?? t.Assignee.UserName) : "未分配")
                    : (t.AgentName ?? "数字员工"),
                IsOverdue = t.EndTime.HasValue && t.EndTime < now,
                IsNearDeadline = t.EndTime.HasValue && t.EndTime >= now && t.EndTime <= threeDaysFromNow
            })
            .ToListAsync();

        // 分组键：0=逾期, 1=临近截止3天, 2=普通, 3=待审核
        // 组内按 DeadlineAt 升序（null 排最后）
        return rows
            .OrderBy(t => t.Status == ToDo.Entities.TaskStatus.PendingConfirmation ? 3 : t.IsOverdue ? 0 : t.IsNearDeadline ? 1 : 2)
            .ThenBy(t => t.DeadlineAt ?? DateTime.MaxValue)
            .ToList();
    }

    /// <summary>按项目加载"上次会议至今已完成"的任务</summary>
    private async Task<Dictionary<int, List<MeetingPrepTaskRow>>> LoadRecentlyCompletedTasksAsync(IEnumerable<int> projectIds)
    {
        var ids = projectIds?.ToList() ?? new List<int>();
        var result = new Dictionary<int, List<MeetingPrepTaskRow>>();
        if (ids.Count == 0) return result;

        // 每个项目的最近会议时间（考虑到 MeetingMinutes 可能不是该项目的主会议，但
        // 草稿选的项目 = 关注点，所以这里按 ProjectId 精确匹配）
        var lastMeetings = await _context.MeetingMinutes
            .AsNoTracking()
            .Where(m => ids.Contains(m.ProjectId) && !m.IsDeleted)
            .GroupBy(m => m.ProjectId)
            .Select(g => new { ProjectId = g.Key, LastMeetingDate = g.Max(m => m.MeetingDate) })
            .ToDictionaryAsync(x => x.ProjectId, x => x.LastMeetingDate);

        // 没开过会的项目：显示"最近两周内"已完成任务作为参照
        var fallbackCutoff = AppTime.Now.AddDays(-14);

        foreach (var pid in ids)
        {
            var cutoff = lastMeetings.TryGetValue(pid, out var lastMeeting) ? lastMeeting : fallbackCutoff;

            var completed = await _context.ToDoTasks
                .Where(t => t.ProjectId == pid && !t.IsDeleted
                    && t.Status == ToDo.Entities.TaskStatus.Completed
                    && t.UpdatedAt > cutoff)
                .Select(t => new MeetingPrepTaskRow
                {
                    Id = t.Id,
                    Title = t.Title,
                    ProjectId = t.ProjectId,
                    ProjectName = t.Project != null ? t.Project.Name : "",
                    Status = t.Status,
                    Priority = t.Priority,
                    DeadlineAt = t.EndTime,
                    AssigneeName = t.AssigneeType == TaskAssigneeType.Human
                        ? (t.Assignee != null ? (t.Assignee.RealName ?? t.Assignee.UserName) : "未分配")
                        : (t.AgentName ?? "数字员工"),
                    IsOverdue = false
                })
                .OrderByDescending(t => t.DeadlineAt)
                .ToListAsync();

            result[pid] = completed;
        }
        return result;
    }

    // --- DTO ---

    public class ProjectOption
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class MeetingPrepTaskRow
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public int ProjectId { get; set; }
        public string ProjectName { get; set; } = string.Empty;
        public ToDo.Entities.TaskStatus Status { get; set; }
        public TaskPriority Priority { get; set; }
        public DateTime? DeadlineAt { get; set; }
        public string AssigneeName { get; set; } = string.Empty;
        public bool IsOverdue { get; set; }
        public bool IsNearDeadline { get; set; }

        public string StatusText => Status switch
        {
            ToDo.Entities.TaskStatus.NotStarted => "未开始",
            ToDo.Entities.TaskStatus.InProgress => "进行中",
            ToDo.Entities.TaskStatus.PendingConfirmation => "待审核",
            _ => Status.ToString()
        };
    }

    // POST body DTO
    public class SaveRequestBody
    {
        public int DraftId { get; set; }
        public string Title { get; set; } = string.Empty;
        public int[]? ProjectIds { get; set; }
        public int[]? TaskIds { get; set; }
    }
}

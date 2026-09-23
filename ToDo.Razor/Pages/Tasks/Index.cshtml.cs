using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;
using TodoTask = ToDo.Entities.ToDoTask;
using ToDoTaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Razor.Pages.Tasks;

public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _dbcontext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAntiforgery _antiforgery;
    private readonly ToDoTaskDomainService _taskDomainService;
    private readonly UserNotificationService _notifications;

    public IndexModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IAntiforgery antiforgery, ToDoTaskDomainService taskDomainService, UserNotificationService notifications)
    {
        _dbcontext = context;
        _userManager = userManager;
        _antiforgery = antiforgery;
        _taskDomainService = taskDomainService;
        _notifications = notifications;
    }

    public List<TodoTask> Tasks { get; set; } = new();
    public bool IsProjectAdmin { get; set; }
    public int CurrentUserId { get; set; }
    public Dictionary<int, bool> TaskIdToCanEdit { get; set; } = new();
    public string RequestVerificationToken { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)] public int? ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public bool ShowDeleted { get; set; }
    [BindProperty(SupportsGet = true)] public bool MyWork { get; set; }
    [BindProperty(SupportsGet = true)] public int? GroupId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Assignee { get; set; }
    [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? EndDate { get; set; }

    public SelectList TaskGroups { get; set; } = default!;
    public SelectList ProjectOptions { get; set; } = default!;
    public List<SelectListItem> StatusList { get; set; } = new();
    public List<SelectListItem> Assignees { get; set; } = new();

    public async Task OnGetAsync(int? projectId, string? keyword, int? groupId, string? status, string? assignee, DateTime? createdFrom, DateTime? createdTo)
    {
        if (!int.TryParse(_userManager.GetUserId(User), out var userId)) return;
        CurrentUserId = userId;

        var isSystemAdmin = User.IsInRole(nameof(UserRole.systemAdmin));
        var accessibleProjectsQuery = _dbcontext.Project.AsNoTracking()
            .Where(item => !item.IsDeleted);
        if (!isSystemAdmin)
        {
            accessibleProjectsQuery = accessibleProjectsQuery.Where(item =>
                item.LeaderUserId == userId
                || _dbcontext.ProjectUsers.Any(member => member.ProjectId == item.Id && member.UserId == userId));
        }

        var accessibleProjects = await accessibleProjectsQuery.OrderBy(item => item.Name).ToListAsync();
        var accessibleProjectIds = accessibleProjects.Select(item => item.Id).ToList();
        ProjectOptions = new SelectList(accessibleProjects, "Id", "Name");
        ProjectId = projectId ?? ProjectId;
        if (ProjectId.HasValue && !accessibleProjectIds.Contains(ProjectId.Value))
        {
            ProjectId = null;
            TempData["Message"] = "所选项目不存在或您无权访问";
        }

        IsProjectAdmin = ProjectId.HasValue && await CanManageProjectAsync(ProjectId.Value, userId);
        var query = _dbcontext.ToDoTasks
            .Where(item => accessibleProjectIds.Contains(item.ProjectId))
            .Include(item => item.Assignee)
            .Include(item => item.AgentDefinition)
            .Include(item => item.Claimer)
            .Include(item => item.Group)
            .Include(item => item.Project)
            .AsQueryable();
        if (ProjectId.HasValue) query = query.Where(item => item.ProjectId == ProjectId.Value);
        if (MyWork)
        {
            var ownIds = new TaskProgressService(_dbcontext).MyActiveTasks(userId).Select(item => item.Id);
            query = query.Where(item => ownIds.Contains(item.Id));
        }
        query = ShowDeleted && IsProjectAdmin
            ? query.Where(item => item.IsDeleted)
            : query.Where(item => !item.IsDeleted);
        var search = string.IsNullOrWhiteSpace(SearchTerm) ? keyword : SearchTerm;
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(item => item.Title.Contains(search) || (item.Description ?? "").Contains(search));
        if (GroupId.HasValue) query = query.Where(item => item.GroupId == GroupId);
        if (EndDate.HasValue)
        {
            var endExclusive = EndDate.Value.Date.AddDays(1);
            query = query.Where(item => item.EndTime < endExclusive);
        }
        if (Enum.TryParse<ToDoTaskStatus>(Status, out var parsedStatus)) query = query.Where(item => item.Status == parsedStatus);
        if (!string.IsNullOrWhiteSpace(Assignee))
        {
            if (Assignee.StartsWith("agent:", StringComparison.Ordinal)
                && int.TryParse(Assignee["agent:".Length..], out var agentId))
            {
                query = query.Where(item => item.AssigneeType == TaskAssigneeType.DigitalEmployee
                    && item.AgentDefinitionId == agentId);
            }
            else
            {
                query = query.Where(item => item.AssigneeType == TaskAssigneeType.Human
                    && item.Assignee != null
                    && item.Assignee.UserName == Assignee);
            }
        }
        Tasks = await query.OrderByDescending(item => item.CreatedAt).ToListAsync();

        RequestVerificationToken = _antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;
        var managedProjectIds = new HashSet<int>();
        if (isSystemAdmin)
        {
            managedProjectIds.UnionWith(accessibleProjectIds);
        }
        else
        {
            managedProjectIds.UnionWith(await _dbcontext.Project.AsNoTracking()
                .Where(item => accessibleProjectIds.Contains(item.Id) && item.LeaderUserId == userId)
                .Select(item => item.Id)
                .ToListAsync());
            managedProjectIds.UnionWith(await _dbcontext.ProjectUsers.AsNoTracking()
                .Where(item => accessibleProjectIds.Contains(item.ProjectId)
                    && item.UserId == userId
                    && item.ProjectRole == (int)ProjectRole.Admin)
                .Select(item => item.ProjectId)
                .ToListAsync());
        }
        foreach (var task in Tasks)
        {
            TaskIdToCanEdit[task.Id] = managedProjectIds.Contains(task.ProjectId) || task.CreatorId == userId;
        }

        var groups = ProjectId.HasValue
            ? await _dbcontext.TaskGroups.Where(item => item.ProjectId == ProjectId.Value && !item.IsDeleted).OrderBy(item => item.Name).ToListAsync()
            : [];
        TaskGroups = new SelectList(groups, "Id", "Name");
        StatusList = Enum.GetValues<ToDoTaskStatus>().Select(item => new SelectListItem(item.ToString(), item.ToString())).ToList();
        var assigneeUserIds = await _dbcontext.ProjectUsers
            .Where(item => accessibleProjectIds.Contains(item.ProjectId)
                && (!ProjectId.HasValue || item.ProjectId == ProjectId.Value))
            .Select(item => item.UserId)
            .Distinct()
            .ToListAsync();
        assigneeUserIds.AddRange(await _dbcontext.Project.AsNoTracking()
            .Where(item => accessibleProjectIds.Contains(item.Id)
                && (!ProjectId.HasValue || item.Id == ProjectId.Value))
            .Select(item => item.LeaderUserId)
            .ToListAsync());
        Assignees = await _dbcontext.Users.AsNoTracking()
            .Where(item => assigneeUserIds.Contains(item.Id) && !item.IsDeleted)
            .OrderBy(item => item.UserName)
            .Select(item => new SelectListItem(string.IsNullOrWhiteSpace(item.RealName)
                ? item.UserName! : item.RealName + " (" + item.UserName + ")", item.UserName!))
            .ToListAsync();
        Assignees.AddRange(await _dbcontext.AgentDefinitions.AsNoTracking()
            .Where(item => item.IsEnabled && item.CanReceiveTaskDispatch)
            .OrderBy(item => item.Name)
            .Select(item => new SelectListItem($"Agent · {item.Name}", $"agent:{item.Id}"))
            .ToListAsync());
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (!int.TryParse(_userManager.GetUserId(User), out var userId)) return Forbid();
        var task = await _dbcontext.ToDoTasks.Include(item => item.Project).FirstOrDefaultAsync(item => item.Id == id);
        if (task == null) return NotFound();
        if (!await CanManageProjectAsync(task.ProjectId, userId)) return Forbid();
        task.IsDeleted = true;
        task.UpdatedAt = AppTime.Now;
        await _dbcontext.SaveChangesAsync();
        await _taskDomainService.LogTaskOperationAsync(OperationType.删除, OperationTarget.任务, userId, projectId: task.ProjectId, taskId: task.Id, taskTitle: task.Title, targetId: task.Id, afterState: "软删除", status: OperationStatus.成功);
        TempData["Message"] = "任务已成功删除";
        return RedirectToPage(new { projectId = task.ProjectId });
    }

    public async Task<IActionResult> OnPostClaimAsync(int id)
    {
        if (!int.TryParse(_userManager.GetUserId(User), out var userId)) return Unauthorized();
        var task = await _dbcontext.ToDoTasks.FirstOrDefaultAsync(item => item.Id == id && !item.IsDeleted);
        if (task == null) return NotFound();
        if (!task.CanBeClaimedByHuman)
        {
            TempData["Message"] = task.AssigneeType == TaskAssigneeType.DigitalEmployee
                ? "数字员工任务不能人工认领，请先通过编辑任务切换执行主体"
                : "该任务当前不能认领";
            return RedirectToPage(new { projectId = task.ProjectId });
        }
        var canAccessProject = User.IsInRole(nameof(UserRole.systemAdmin))
            || await _dbcontext.Project.AsNoTracking().AnyAsync(item => item.Id == task.ProjectId && !item.IsDeleted && item.LeaderUserId == userId)
            || await _dbcontext.ProjectUsers.AnyAsync(item => item.ProjectId == task.ProjectId && item.UserId == userId);
        if (!canAccessProject) return Forbid();
        task.AssigneeId = userId;
        task.ClaimerId = userId;
        task.AssigneeType = TaskAssigneeType.Human;
        task.SetStatus(ToDoTaskStatus.InProgress);
        task.StartTime ??= AppTime.Now;
        task.UpdatedAt = AppTime.Now;
        await _dbcontext.SaveChangesAsync();
        if (task.CreatorId != userId) await _notifications.NotifyAsync(task.CreatorId, "任务已被认领", $"任务「{task.Title}」已被成员认领", "Task", $"/Tasks/Details/{task.Id}");
        TempData["Message"] = "任务认领成功";
        return RedirectToPage(new { projectId = task.ProjectId });
    }

    public async Task<IActionResult> OnPostRestoreAsync(int id, int projectId)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Forbid();
        if (!await CanManageProjectAsync(projectId, userId)) return Forbid();
        var task = await _dbcontext.ToDoTasks.FirstOrDefaultAsync(item => item.Id == id && item.ProjectId == projectId);
        if (task == null) return NotFound();
        task.IsDeleted = false;
        task.UpdatedAt = AppTime.Now;
        await _dbcontext.SaveChangesAsync();
        await _taskDomainService.LogTaskOperationAsync(OperationType.恢复, OperationTarget.任务, userId, projectId: projectId, taskId: id, taskTitle: task.Title, targetId: id, afterState: "恢复任务", status: OperationStatus.成功);
        TempData["Message"] = "任务已恢复";
        return RedirectToPage(new { projectId });
    }

    private async Task<bool> CanManageProjectAsync(int projectId, int userId)
    {
        if (User.IsInRole(nameof(UserRole.systemAdmin))) return true;
        if (await _dbcontext.Project.AsNoTracking().AnyAsync(project =>
            project.Id == projectId && !project.IsDeleted && project.LeaderUserId == userId)) return true;
        return await _dbcontext.ProjectUsers.AsNoTracking().AnyAsync(member =>
            member.ProjectId == projectId
            && member.UserId == userId
            && member.ProjectRole == (int)ProjectRole.Admin);
    }
}

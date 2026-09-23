using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Agents;

[Authorize]
public class IndexModel : PageModel
{
    private readonly IAgentRegistry _registry;
    private readonly AgentRunQueueService _runQueue;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AiSessionService _sessions;
    private readonly ApplicationDbContext _context;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(IAgentRegistry registry, AgentRunQueueService runQueue, UserManager<ApplicationUser> userManager, AiSessionService sessions, ApplicationDbContext context, ILogger<IndexModel> logger)
    {
        _registry = registry;
        _runQueue = runQueue;
        _userManager = userManager;
        _sessions = sessions;
        _context = context;
        _logger = logger;
    }

    public IReadOnlyList<AgentDefinition> Agents { get; set; } = Array.Empty<AgentDefinition>();
    public List<AiSession> RecentSessions { get; set; } = new();
    public IReadOnlyDictionary<int, string> ProjectNames { get; set; } = new Dictionary<int, string>();
    public IReadOnlyDictionary<int, string> TaskTitles { get; set; } = new Dictionary<int, string>();
    public string LastResponse { get; set; } = string.Empty;

    public List<Project> Projects { get; set; } = new();
    public List<ToDoTask> Tasks { get; set; } = new();
    public bool IsSystemAdmin { get; set; }

    [BindProperty] public string AgentKey { get; set; } = "project-workbench";
    [BindProperty, StringLength(8000, ErrorMessage = "Agent 指令不能超过 8000 个字符")] public string Prompt { get; set; } = string.Empty;
    [BindProperty] public int? ProjectId { get; set; }
    [BindProperty] public int? TaskId { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");
        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostRunAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");
        if (user.Role != UserRole.systemAdmin) return Forbid();
        await LoadAsync(user);
        if (string.IsNullOrWhiteSpace(AgentKey)) ModelState.AddModelError(nameof(AgentKey), "请选择 Agent");
        if (string.IsNullOrWhiteSpace(Prompt)) ModelState.AddModelError(nameof(Prompt), "请输入 Agent 指令");

        try
        {
            var definition = Agents.FirstOrDefault(item =>
                item.IsEnabled && string.Equals(item.AgentKey, AgentKey, StringComparison.OrdinalIgnoreCase));
            if (definition == null)
                ModelState.AddModelError(nameof(AgentKey), "Agent 不存在或已停用");

            if (TaskId.HasValue)
            {
                var taskProjectId = await _context.ToDoTasks.Where(item => item.Id == TaskId.Value && !item.IsDeleted).Select(item => (int?)item.ProjectId).FirstOrDefaultAsync();
                if (!taskProjectId.HasValue) throw new InvalidOperationException("任务不存在或已删除");
                ProjectId = taskProjectId;
            }
            if (definition?.RequiresTask == true && !TaskId.HasValue)
                ModelState.AddModelError(nameof(TaskId), "该 Agent 必须选择任务");
            if (definition?.RequiresProject == true && !ProjectId.HasValue && !TaskId.HasValue)
                ModelState.AddModelError(nameof(ProjectId), "该 Agent 必须选择项目");
            if (!ModelState.IsValid) return Page();

            var job = await _runQueue.EnqueueNewAsync(AgentKey, Prompt.Trim(), user, ProjectId, TaskId, HttpContext.RequestAborted);
            return RedirectToPage("/AiSessions/Details", new { id = job.AiSessionId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue manual Agent run for user {UserId}", user.Id);
            ModelState.AddModelError(string.Empty, "Agent 任务入队失败，请稍后重试");
        }
        return Page();
    }

    private async Task LoadAsync(ApplicationUser user)
    {
        Agents = await _registry.GetAllAsync();
        IsSystemAdmin = user.Role == UserRole.systemAdmin;
        RecentSessions = await _sessions.GetRecentAsync(user, 8);
        var projectQuery = _context.Project.AsNoTracking().Where(item => !item.IsDeleted && item.Status == ProjectStatus.Active);
        if (user.Role != UserRole.systemAdmin)
            projectQuery = projectQuery.Where(project => project.LeaderUserId == user.Id
                || _context.ProjectUsers.Any(member => member.ProjectId == project.Id && member.UserId == user.Id));
        Projects = await projectQuery.OrderBy(item => item.Name).ToListAsync();
        var projectIds = Projects.Select(item => item.Id).ToList();
        Tasks = await _context.ToDoTasks.AsNoTracking().Where(item => projectIds.Contains(item.ProjectId) && !item.IsDeleted)
            .OrderBy(item => item.ProjectId).ThenBy(item => item.Title).Take(500).ToListAsync();
        ProjectNames = Projects.ToDictionary(item => item.Id, item => item.Name);
        TaskTitles = Tasks.ToDictionary(item => item.Id, item => item.Title);
    }
}

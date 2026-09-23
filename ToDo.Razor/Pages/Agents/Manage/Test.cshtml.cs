using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Agents.Manage;

[Authorize(Roles = "systemAdmin")]
public sealed class TestModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly AgentTestingService _testing;
    private readonly AgentAdministrationService _administration;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<TestModel> _logger;

    public TestModel(
        ApplicationDbContext context,
        AgentTestingService testing,
        AgentAdministrationService administration,
        UserManager<ApplicationUser> userManager,
        ILogger<TestModel> logger)
    {
        _context = context;
        _testing = testing;
        _administration = administration;
        _userManager = userManager;
        _logger = logger;
    }

    public AgentDefinition Agent { get; private set; } = null!;
    public List<AgentTestRun> RecentRuns { get; private set; } = [];
    public List<ScopeOption> ProjectOptions { get; private set; } = [];
    public List<ScopeOption> TaskOptions { get; private set; } = [];

    [BindProperty]
    public int? ProjectId { get; set; }

    [BindProperty]
    public int? TaskId { get; set; }

    [BindProperty]
    public string Prompt { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        return await LoadAsync(id) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostRunAsync(int id)
    {
        try
        {
            var user = await _userManager.GetUserAsync(User)
                ?? throw new UnauthorizedAccessException("当前用户不存在");
            var run = await _testing.RunAsync(
                id,
                user.Id,
                ProjectId,
                TaskId,
                Prompt,
                HttpContext.RequestAborted);
            if (run.Status == AgentTestRunStatus.Passed)
                TempData["SuccessMessage"] = "隔离测试已通过，当前版本可以发布。";
            else
                TempData["ErrorMessage"] = run.ValidationSummary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent 隔离测试失败，AgentId={AgentId}", id);
            TempData["ErrorMessage"] = ex.Message;
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostPublishAsync(int id)
    {
        try
        {
            var user = await _userManager.GetUserAsync(User);
            await _administration.PublishAsync(id, user?.Id, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = "Agent 已发布，可以接收授权范围内的任务。";
            return RedirectToPage("./Index");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发布 Agent 失败，AgentId={AgentId}", id);
            TempData["ErrorMessage"] = ex.Message;
            return RedirectToPage(new { id });
        }
    }

    private async Task<bool> LoadAsync(int id)
    {
        Agent = await _context.AgentDefinitions.AsNoTracking()
            .Include(item => item.AcceptanceContract)
            .Include(item => item.ToolPermissions)
            .FirstOrDefaultAsync(item => item.Id == id) ?? null!;
        if (Agent == null) return false;

        Prompt = string.IsNullOrWhiteSpace(Prompt) ? Agent.AcceptanceContract?.TestPrompt ?? string.Empty : Prompt;
        ProjectOptions = await _context.Project.AsNoTracking()
            .Where(item => !item.IsDeleted)
            .OrderBy(item => item.Name)
            .Select(item => new ScopeOption(item.Id, item.Name, null))
            .ToListAsync();
        TaskOptions = await _context.ToDoTasks.AsNoTracking()
            .Where(item => !item.IsDeleted)
            .OrderByDescending(item => item.CreatedAt)
            .Take(500)
            .Select(item => new ScopeOption(item.Id, $"#{item.Id} {item.Title}", item.ProjectId))
            .ToListAsync();
        RecentRuns = await _context.AgentTestRuns.AsNoTracking()
            .Include(item => item.RequestedByUser)
            .Include(item => item.AiSession)
            .Where(item => item.AgentDefinitionId == id)
            .OrderByDescending(item => item.StartedAt)
            .Take(10)
            .ToListAsync();
        return true;
    }

    public sealed record ScopeOption(int Id, string Label, int? ProjectId);
}

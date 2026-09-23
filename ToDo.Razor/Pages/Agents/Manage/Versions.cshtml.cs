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
public sealed class VersionsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly AgentAdministrationService _administration;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<VersionsModel> _logger;
    public VersionsModel(
        ApplicationDbContext context,
        AgentAdministrationService administration,
        UserManager<ApplicationUser> userManager,
        ILogger<VersionsModel> logger)
    {
        _context = context;
        _administration = administration;
        _userManager = userManager;
        _logger = logger;
    }
    public AgentDefinition Agent { get; private set; } = null!;
    public List<AgentDefinitionVersion> Items { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Agent = await _context.AgentDefinitions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id) ?? null!;
        if (Agent == null) return NotFound();
        Items = await _context.AgentDefinitionVersions.AsNoTracking().Include(item => item.ChangedByUser)
            .Where(item => item.AgentDefinitionId == id).OrderByDescending(item => item.Version).ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRollbackAsync(int id, int version)
    {
        try
        {
            var user = await _userManager.GetUserAsync(User);
            await _administration.RollbackToVersionAsync(id, version, user?.Id, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = $"已基于 v{version} 创建并发布新的稳定回滚版本";
            return RedirectToPage("./Index");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "回滚 Agent 失败，AgentId={AgentId}, Version={Version}", id, version);
            TempData["ErrorMessage"] = ex.Message;
            return RedirectToPage(new { id });
        }
    }
}

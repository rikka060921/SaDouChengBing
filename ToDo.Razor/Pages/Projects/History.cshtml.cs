using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Projects;

[Authorize]
public sealed class HistoryModel(
    ApplicationDbContext context,
    ProjectDomain projects,
    UserManager<ApplicationUser> users) : PageModel
{
    [BindProperty(SupportsGet = true)] public int ProjectId { get; set; }
    [BindProperty] public string ImportJson { get; set; } = string.Empty;
    public string ProjectName { get; private set; } = string.Empty;
    public bool CanImport { get; private set; }
    public List<ProjectHistoryEntry> Entries { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostImportAsync(CancellationToken cancellationToken)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await projects.CanViewProjectAsync(ProjectId, user.Id, user.Role)) return Forbid();
        if (!await projects.CanManageProjectAsync(ProjectId, user.Id, user.Role)) return Forbid();
        if (!await context.Project.AsNoTracking().AnyAsync(p => p.Id == ProjectId && !p.IsDeleted
                && p.Status == ProjectStatus.Active, cancellationToken))
            return BadRequest(ProjectLifecycleRules.ReadOnlyMessage);

        IReadOnlyList<ProjectHistoryImportItem> items;
        try { items = ProjectHistoryImport.Parse(ImportJson); }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(nameof(ImportJson), ex.Message);
            return await LoadAsync(cancellationToken);
        }

        var keys = items.Select(item => item.Key).ToArray();
        var existing = (await context.ProjectHistoryEntries.AsNoTracking()
                .Where(item => item.ProjectId == ProjectId && keys.Contains(item.SourceKey))
                .Select(item => item.SourceKey)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = items.Where(item => !existing.Contains(item.Key)).ToList();
        context.ProjectHistoryEntries.AddRange(missing.Select(item => new ProjectHistoryEntry
        {
            ProjectId = ProjectId,
            SourceKey = item.Key,
            Period = item.Period,
            SortDate = item.SortDate.Date,
            Title = item.Title,
            Summary = item.Summary,
            Sources = item.Sources,
            ImportedByUserId = user.Id,
            ImportedAt = AppTime.Now
        }));
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            ModelState.AddModelError(nameof(ImportJson), "保存失败；可能有其他人同时导入，请刷新后重试");
            return await LoadAsync(cancellationToken);
        }

        TempData["HistoryMessage"] = $"已新增 {missing.Count} 条历史节点，跳过 {existing.Count} 条已导入节点";
        return RedirectToPage(new { projectId = ProjectId });
    }

    private async Task<IActionResult> LoadAsync(CancellationToken cancellationToken)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await projects.CanViewProjectAsync(ProjectId, user.Id, user.Role)) return Forbid();
        var project = await context.Project.AsNoTracking()
            .Where(item => item.Id == ProjectId && !item.IsDeleted)
            .Select(item => new { item.Name, item.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (project is null) return NotFound();
        ProjectName = project.Name;
        CanImport = project.Status == ProjectStatus.Active
            && await projects.CanManageProjectAsync(ProjectId, user.Id, user.Role);
        Entries = await context.ProjectHistoryEntries.AsNoTracking()
            .Where(item => item.ProjectId == ProjectId)
            .OrderByDescending(item => item.SortDate)
            .ThenByDescending(item => item.Id)
            .ToListAsync(cancellationToken);
        return Page();
    }
}

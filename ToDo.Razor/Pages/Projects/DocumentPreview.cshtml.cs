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
public sealed class DocumentPreviewModel(ApplicationDbContext context, ProjectDocumentService documents, UserManager<ApplicationUser> users) : PageModel
{
    [BindProperty(SupportsGet = true)] public int ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public int DocumentId { get; set; }
    public string FileName { get; private set; } = string.Empty;
    public int VersionNumber { get; private set; }
    public bool IsCurrent { get; private set; }
    public bool CanEdit { get; private set; }
    public ProjectDocumentContent PreviewContent { get; private set; } = new(string.Empty, string.Empty, false);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        var actor = await users.GetUserAsync(User);
        if (actor == null) return Challenge();
        if (!await documents.CanAccessProjectMaterialsAsync(ProjectId, actor, cancellationToken)) return Forbid();

        var document = await context.ProjectDocuments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == ProjectId && item.Id == DocumentId, cancellationToken);
        if (document == null || !DocumentModelPolicy.IsMarkdown(document.FileName)) return NotFound();

        FileName = document.FileName;
        VersionNumber = document.VersionNumber;
        IsCurrent = document.IsCurrent;
        CanEdit = IsCurrent && await context.Project.AnyAsync(project => project.Id == ProjectId
            && project.Status == ProjectStatus.Active && !project.IsDeleted, cancellationToken);
        PreviewContent = await ProjectDocumentIndexService.ReadContentAsync(documents.ResolveStoragePath(document.StoragePath),
            document.FileName, cancellationToken);
        return Page();
    }
}

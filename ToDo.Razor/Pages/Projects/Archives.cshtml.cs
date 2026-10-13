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
public sealed class ArchivesModel(ApplicationDbContext context, ProjectDomain projects,
    ProjectDocumentService documents, UserManager<ApplicationUser> users,
    ILogger<ArchivesModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public int ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public string Kind { get; set; } = "task";
    [BindProperty(SupportsGet = true)] public string? Key { get; set; }
    public string ProjectName { get; private set; } = string.Empty;
    public ProjectArchivePackage? Archive { get; private set; }
    public ProjectArchiveRecord? Selected { get; private set; }
    public List<ProjectArchiveRecord> Records { get; private set; } = [];
    public int? DocumentId { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await projects.CanViewProjectAsync(ProjectId, user.Id, user.Role)
            || !await ProjectArchiveAccess.CanReadAsync(context, ProjectId, user.Id, user.Role, cancellationToken)) return Forbid();
        ProjectName = await context.Project.AsNoTracking().Where(p => p.Id == ProjectId && !p.IsDeleted)
            .Select(p => p.Name).FirstAsync(cancellationToken);
        Kind = Kind == "meeting" ? "meeting" : "task";
        var document = await context.ProjectDocuments.AsNoTracking()
            .Where(d => d.ProjectId == ProjectId && d.IsCurrent && d.FileName == ProjectArchivePackage.FileName)
            .OrderByDescending(d => d.VersionNumber).ThenByDescending(d => d.Id).FirstOrDefaultAsync(cancellationToken);
        if (document is null) return Page();
        DocumentId = document.Id;
        try
        {
            var resolved = await documents.ResolveDownloadAsync(document.Id, ProjectId, cancellationToken);
            if (resolved is null || new FileInfo(resolved.Value.FullPath).Length > ProjectArchivePackage.MaximumBytes)
                throw new ArgumentException("档案文件不存在或超过大小限制");
            Archive = ProjectArchivePackage.Parse(await System.IO.File.ReadAllTextAsync(
                resolved.Value.FullPath, cancellationToken), ProjectId);
            Records = Archive.Records.Where(r => r.Kind == Kind).ToList();
            if (!string.IsNullOrEmpty(Key))
            {
                Selected = Records.FirstOrDefault(r => r.Key == Key);
                if (Selected is null) return NotFound();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Unable to read historical archive document {DocumentId} in project {ProjectId}", document.Id, ProjectId);
            Error = "历史档案文件读取或校验失败，请项目管理员检查资料版本。";
        }
        return Page();
    }
}

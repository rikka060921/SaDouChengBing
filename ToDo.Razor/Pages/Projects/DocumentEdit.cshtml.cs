using System.ComponentModel.DataAnnotations;
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
public sealed class DocumentEditModel(ApplicationDbContext context, ProjectDocumentService documents, UserManager<ApplicationUser> users) : PageModel
{
    [BindProperty(SupportsGet = true)] public int ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public int DocumentId { get; set; }
    [BindProperty(SupportsGet = true)] public int DraftId { get; set; }
    [BindProperty, Required(ErrorMessage = "请输入资料正文"), StringLength(ProjectDocumentEditingService.MaxCharacters, ErrorMessage = "正文最多 10 万字符")]
    public string EditedContent { get; set; } = string.Empty;
    [BindProperty, Required(ErrorMessage = "资料版本标识丢失，请先复制输入，再刷新页面")]
    public string ExpectedToken { get; set; } = string.Empty;
    public DocumentEditSnapshot? Snapshot { get; set; }
    public async Task<IActionResult> OnGetAsync() => await LoadAsync(true);
    private async Task<IActionResult> LoadAsync(bool initialize)
    {
        Response.Headers.CacheControl = "private, no-store";
        var user = await users.GetUserAsync(User); if (user == null) return Challenge();
        try
        {
            Snapshot = await new ProjectDocumentEditingService(context, documents).ReadAsync(ProjectId, DocumentId, DraftId, user.Id, HttpContext.RequestAborted);
            if (initialize) { EditedContent = Snapshot.Content; ExpectedToken = Snapshot.Token; }
            return Page();
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { ModelState.AddModelError(string.Empty, ex.Message); return Page(); }
        catch (IOException ex)
        {
            HttpContext.RequestServices.GetService<ILogger<DocumentEditModel>>()?.LogError(ex, "Reading document editor failed for project {ProjectId}", ProjectId);
            ModelState.AddModelError(string.Empty, "暂时无法读取资料文件。已有输入仍保留，请先复制备份，再稍后重试。");
            return Page();
        }
    }
    public async Task<IActionResult> OnPostAsync()
    {
        var user = await users.GetUserAsync(User); if (user == null) return Challenge();
        try
        {
            if (!ModelState.IsValid) return await LoadAsync(false);
            await new ProjectDocumentEditingService(context, documents).SaveAsync(ProjectId, DocumentId, DraftId, user.Id, ExpectedToken, EditedContent, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = DraftId > 0 ? "草稿校订已保存，仍需审核后才成为正式资料。" : "正文已保存为新版本，原版本仍可查看。";
            return RedirectToPage("./Materials", new { projectId = ProjectId });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException) { ModelState.AddModelError(string.Empty, "资料已变化，你的输入已保留，请复制后刷新对比。"); }
        catch (InvalidOperationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HttpContext.RequestServices.GetService<ILogger<DocumentEditModel>>()?.LogError(ex, "Saving project document {ProjectId}/{DocumentId}/{DraftId} failed", ProjectId, DocumentId, DraftId);
            ModelState.AddModelError(string.Empty, "保存未成功，你的输入仍保留，请复制备份后重试。");
            // Do not immediately query the same failed database/file service and lose the submitted text.
            return Page();
        }
        context.ChangeTracker.Clear();
        return await LoadAsync(false);
    }
}

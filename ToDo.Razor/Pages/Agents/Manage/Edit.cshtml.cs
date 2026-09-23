using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Agents.Manage;

[Authorize(Roles = "systemAdmin")]
public class EditModel(AgentAdministrationService service, UserManager<ApplicationUser> users,
    ILogger<EditModel> logger) : PageModel
{
    [BindProperty] public int? Id { get; set; }
    [BindProperty] public int ExpectedVersion { get; set; }
    [BindProperty] public AgentProfileInput Input { get; set; } = new();
    public AgentDefinition Agent { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (!id.HasValue) return RedirectToPage("./Create");
        Id = id;
        var agent = await service.GetAsync(id.Value, HttpContext.RequestAborted);
        if (agent == null) return NotFound();
        Agent = agent;
        ExpectedVersion = agent.Version;
        Input = new() { Name = agent.Name, Description = agent.Description, Instructions = agent.SystemPrompt };
        Response.Headers.CacheControl = "no-store";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!Id.HasValue) return BadRequest();
        var user = await users.GetUserAsync(User);
        if (user == null) return Challenge();
        var agent = await service.GetAsync(Id.Value, HttpContext.RequestAborted);
        if (agent == null) return NotFound();
        Agent = agent;
        if (!ModelState.IsValid) return Page();
        try
        {
            var saved = await service.SaveProfileAsync(Id.Value, ExpectedVersion, Input, user.Id, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = $"「{saved.Name}」已保存，{(saved.IsEnabled ? "后续执行使用新的工作要求" : "仍保持停用")}。原有模型和授权未改变。";
            return RedirectToPage("./Index");
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.DataAnnotations.ValidationException)
        { ModelState.AddModelError(string.Empty, ex.Message); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存 Agent 工作要求失败，AgentId={AgentId}", Id);
            ModelState.AddModelError(string.Empty, "保存失败，请稍后重试；本次输入仍保留在下方。");
        }
        return Page();
    }
}

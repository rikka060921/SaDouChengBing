using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Agents.Manage;

[Authorize(Roles = "systemAdmin")]
public class CreateModel(AgentSetupService setup,
    UserManager<ApplicationUser> users) : PageModel
{
    [BindProperty] public int? Id { get; set; }
    [BindProperty] public AgentSetupInput Input { get; set; } = new();
    public IReadOnlyList<AgentPurpose> Purposes => AgentSetupService.Purposes;

    public Task<IActionResult> OnGetAsync(int? id)
    {
        Id = id;
        if (!id.HasValue) return Task.FromResult<IActionResult>(Page());
        // 旧收藏链接也统一进入支持版本检查的业务编辑页，避免覆盖技术配置。
        return Task.FromResult<IActionResult>(RedirectToPage("./Edit", new { id }));
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Id.HasValue) return RedirectToPage("./Edit", new { id = Id.Value });
        if (!ModelState.IsValid) return Page();
        var user = await users.GetUserAsync(User);
        if (user == null) return Challenge();
        try
        {
            var agent = await setup.SaveAsync(Id, Input, user.Id, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = $"「{agent.Name}」已保存，当前{(agent.IsEnabled ? "已启用，可按用途自动接单" : "仍为停用状态")}。";
            return RedirectToPage("./Index");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.DataAnnotations.ValidationException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }
}

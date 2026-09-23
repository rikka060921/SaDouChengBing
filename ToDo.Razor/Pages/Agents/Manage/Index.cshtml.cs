using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Agents.Manage;

[Authorize(Roles = "systemAdmin")]
public class IndexModel : PageModel
{
    private readonly AgentAdministrationService _service;
    private readonly IAgentRegistry _registry;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        AgentAdministrationService service,
        IAgentRegistry registry,
        UserManager<ApplicationUser> userManager,
        ILogger<IndexModel> logger)
    {
        _service = service;
        _registry = registry;
        _userManager = userManager;
        _logger = logger;
    }

    public List<AgentDefinition> Agents { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Agents = await _service.GetAllAsync();
    }

    public async Task<IActionResult> OnPostEnableAsync(int id)
        => await ExecuteAsync(id, "启用 Agent", async user =>
        {
            await _service.EnableAsync(id, user?.Id, HttpContext.RequestAborted);
            return "Agent 已启用，工具权限与人工审批规则保持不变";
        });

    public async Task<IActionResult> OnPostPublishAsync(int id)
    {
        try
        {
            var user = await _userManager.GetUserAsync(User);
            await _service.PublishAsync(id, user?.Id, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = "Agent 已发布";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发布 Agent 失败，AgentId={AgentId}", id);
            TempData["ErrorMessage"] = ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPauseAsync(int id)
    {
        try
        {
            var user = await _userManager.GetUserAsync(User);
            await _service.PauseAsync(id, user?.Id, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = "Agent 已暂停，不再接收新的任务";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "暂停 Agent 失败，AgentId={AgentId}", id);
            TempData["ErrorMessage"] = ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostArchiveAsync(int id)
    {
        try
        {
            var user = await _userManager.GetUserAsync(User);
            await _service.ArchiveAsync(id, user?.Id, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = "Agent 已归档";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "归档 Agent 失败，AgentId={AgentId}", id);
            TempData["ErrorMessage"] = ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStartCanaryAsync(int id, int canaryPercent = 10)
        => await ExecuteAsync(id, "启动灰度", async user =>
        {
            await _service.StartCanaryAsync(id, canaryPercent, user?.Id, HttpContext.RequestAborted);
            return $"已将 {canaryPercent}% 新流量切到候选版本";
        });

    public async Task<IActionResult> OnPostPromoteCanaryAsync(int id)
        => await ExecuteAsync(id, "晋级灰度", async user =>
        {
            await _service.PromoteCanaryAsync(id, user?.Id, HttpContext.RequestAborted);
            return "灰度版本已晋级为稳定版本";
        });

    public async Task<IActionResult> OnPostStopCanaryAsync(int id)
        => await ExecuteAsync(id, "停止灰度", async user =>
        {
            await _service.StopCanaryAsync(id, user?.Id, HttpContext.RequestAborted);
            return "灰度流量已全部切回稳定版本";
        });

    public async Task<IActionResult> OnPostApplyManagedUpdateAsync(int id)
        => await ExecuteAsync(id, "应用内置 Agent 更新", async user =>
        {
            await _registry.ApplyManagedUpdateAsync(id, user?.Id, HttpContext.RequestAborted);
            return "系统内置 Agent 已更新，并作为新的稳定版本发布";
        });

    private async Task<IActionResult> ExecuteAsync(
        int id,
        string operation,
        Func<ApplicationUser?, Task<string>> action)
    {
        try
        {
            var user = await _userManager.GetUserAsync(User);
            TempData["SuccessMessage"] = await action(user);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Operation}失败，AgentId={AgentId}", operation, id);
            TempData["ErrorMessage"] = ex.Message;
        }
        return RedirectToPage();
    }
}

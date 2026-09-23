using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Agents.Manage;

[Authorize(Roles = "systemAdmin")]
public class TechnicalModel : PageModel
{
    private readonly AgentAdministrationService _service;
    private readonly AgentTemplateCatalog _templates;
    private readonly IAgentToolCatalog _toolCatalog;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<TechnicalModel> _logger;

    public TechnicalModel(
        AgentAdministrationService service,
        AgentTemplateCatalog templates,
        IAgentToolCatalog toolCatalog,
        UserManager<ApplicationUser> userManager,
        ILogger<TechnicalModel> logger)
    {
        _service = service;
        _templates = templates;
        _toolCatalog = toolCatalog;
        _userManager = userManager;
        _logger = logger;
    }

    [BindProperty]
    public int? Id { get; set; }

    [BindProperty]
    public AgentDefinition Input { get; set; } = NewDefinition();

    [BindProperty]
    public AgentAcceptanceContract ContractInput { get; set; } = new();

    [BindProperty]
    public string CapabilityTags { get; set; } = string.Empty;

    [BindProperty]
    public List<AgentContextSource> ContextSources { get; set; } = [];

    [BindProperty]
    public List<string> EnabledTools { get; set; } = [];

    [BindProperty]
    public List<string> ApprovalTools { get; set; } = [];

    public IReadOnlyList<AgentToolDescriptor> ToolCatalog => _toolCatalog.GetAll();
    public IReadOnlyList<AgentTemplateDefinition> Templates => _templates.GetAll();
    public IReadOnlyList<AgentContextSource> ContextSourceOptions { get; } = Enum.GetValues<AgentContextSource>();

    public async Task<IActionResult> OnGetAsync(int? id, string? template)
    {
        Id = id;
        if (!id.HasValue)
        {
            return RedirectToPage("./Create");
        }

        var definition = await _service.GetAsync(id.Value);
        if (definition == null) return NotFound();
        Input = definition;
        ContractInput = definition.AcceptanceContract ?? new AgentAcceptanceContract();
        CapabilityTags = string.Join('\n', AgentAdministrationService.ParseCapabilities(definition.CapabilitiesJson));
        ContextSources = AgentAdministrationService.ParseContextSources(definition.ContextSourcesJson).ToList();
        EnabledTools = definition.ToolPermissions.Where(item => item.IsEnabled).Select(item => item.ToolName).ToList();
        ApprovalTools = definition.ToolPermissions
            .Where(item => item.IsEnabled && item.ReviewMode == AgentToolReviewMode.HumanApproval)
            .Select(item => item.ToolName)
            .ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ModelState.Remove("Input.ToolPermissions");
        ModelState.Remove("Input.AcceptanceContract");
        ModelState.Remove("Input.TestRuns");
        ModelState.Remove("ContractInput.AgentDefinition");
        // 留空表示沿用 appsettings/User Secrets 中的全局模型。
        ModelState.Remove("Input.ModelName");
        if (!ModelState.IsValid) return Page();

        try
        {
            var user = await _userManager.GetUserAsync(User);
            var capabilityTags = AgentAdministrationService.SplitTerms(CapabilityTags);
            var saved = await _service.SaveAsync(
                Id,
                Input,
                ContextSources,
                capabilityTags,
                EnabledTools,
                ApprovalTools,
                ContractInput,
                user?.Id,
                HttpContext.RequestAborted,
                applyImmediately: true);
            TempData["SuccessMessage"] = $"Agent「{saved.Name}」已保存，当前为{(saved.IsEnabled ? "启用" : "停用")}状态。工具审批规则保持不变。";
            return RedirectToPage("./Index");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存 Agent 定义失败，AgentId={AgentId}", Id);
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public string GetContextLabel(AgentContextSource source)
    {
        return source switch
        {
            AgentContextSource.Project => "项目基本信息",
            AgentContextSource.ProjectTasks => "项目任务列表",
            AgentContextSource.SelectedTask => "当前任务及子任务",
            AgentContextSource.TaskComments => "当前任务评论",
            AgentContextSource.Meetings => "最近会议纪要",
            AgentContextSource.Documents => "已授权项目资料",
            AgentContextSource.Reports => "最近日报与报告",
            _ => source.ToString()
        };
    }

    private static AgentDefinition NewDefinition()
    {
        return new AgentDefinition
        {
            Temperature = 0.3,
            MaxTokens = 6000,
            MaxTurns = 20,
            TimeoutSeconds = 90,
            IsEnabled = true,
            CanReceiveTaskDispatch = true
        };
    }

    private void ApplyTemplate(AgentTemplateDefinition template)
    {
        Input = NewDefinition();
        Input.TemplateKey = template.Key;
        Input.Name = template.SuggestedName;
        Input.AgentKey = template.SuggestedKey;
        Input.Description = template.Description;
        Input.SystemPrompt = template.SystemPrompt;
        Input.RequiresProject = template.RequiresProject;
        Input.RequiresTask = template.RequiresTask;
        Input.AutoCommentOnCompletion = template.AutoCommentOnCompletion;
        Input.CanReceiveTaskDispatch = template.CanReceiveTaskDispatch;
        CapabilityTags = string.Join('\n', template.CapabilityTags);
        ContextSources = template.ContextSources.ToList();
        EnabledTools = template.EnabledTools.ToList();
        ApprovalTools = template.ApprovalTools.ToList();
        ContractInput = new AgentAcceptanceContract
        {
            Objective = template.Contract.Objective,
            InputRequirements = template.Contract.InputRequirements,
            RequiredOutput = template.Contract.RequiredOutput,
            SuccessCriteria = template.Contract.SuccessCriteria,
            ProhibitedActions = template.Contract.ProhibitedActions,
            TestPrompt = template.Contract.TestPrompt,
            ExpectedOutputTerms = template.Contract.ExpectedOutputTerms,
            ForbiddenOutputTerms = template.Contract.ForbiddenOutputTerms
        };
    }
}

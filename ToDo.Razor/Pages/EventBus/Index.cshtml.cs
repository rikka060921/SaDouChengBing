using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.EventBus;

[Authorize(Roles = "systemAdmin")]
public class IndexModel : PageModel
{
    private readonly IEventBus _eventBus;
    private readonly ApplicationDbContext _context;
    private readonly AgentEventSubscriptionService _subscriptions;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        IEventBus eventBus,
        ApplicationDbContext context,
        AgentEventSubscriptionService subscriptions,
        UserManager<ApplicationUser> userManager,
        ILogger<IndexModel> logger)
    {
        _eventBus = eventBus;
        _context = context;
        _subscriptions = subscriptions;
        _userManager = userManager;
        _logger = logger;
    }

    public List<EventBusMessage> Messages { get; private set; } = [];
    public List<AgentEventSubscription> Rules { get; private set; } = [];
    public List<AgentEventExecution> Executions { get; private set; } = [];
    public List<AgentDefinition> Agents { get; private set; } = [];
    public List<Project> Projects { get; private set; } = [];

    [BindProperty] public AgentEventSubscription Rule { get; set; } = NewRule();
    [BindProperty] public string EventType { get; set; } = "manual.test";
    [BindProperty] public string PayloadJson { get; set; } = "{}";

    public async Task OnGetAsync(int? editId)
    {
        await LoadAsync();
        if (editId.HasValue)
        {
            Rule = await _context.AgentEventSubscriptions.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == editId.Value) ?? NewRule();
        }
    }

    public async Task<IActionResult> OnPostSaveRuleAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        try
        {
            var saved = await _subscriptions.SaveAsync(Rule, user.Id, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = $"订阅规则“{saved.Name}”已保存";
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存 Agent 事件订阅规则失败，RuleId={RuleId}", Rule.Id);
            TempData["ErrorMessage"] = "保存订阅规则失败，请稍后重试";
            return RedirectToPage(new { editId = Rule.Id > 0 ? (int?)Rule.Id : null });
        }
    }

    public async Task<IActionResult> OnPostToggleRuleAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        try
        {
            await _subscriptions.ToggleAsync(id, user.Id, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = "订阅规则状态已更新";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切换 Agent 事件订阅规则失败，RuleId={RuleId}", id);
            TempData["ErrorMessage"] = "更新订阅规则失败，请稍后重试";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPublishAsync()
    {
        if (!AgentEventSubscriptionService.IsEventTypeAllowed(EventType))
        {
            TempData["ErrorMessage"] = "事件类型格式不正确，或属于禁止订阅的 Agent 内部事件";
            return RedirectToPage();
        }

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(PayloadJson) ? "{}" : PayloadJson);
            await _eventBus.PublishAsync(EventType.Trim().ToLowerInvariant(), document.RootElement.Clone(), "Manual", "manual");
            TempData["SuccessMessage"] = "测试事件已发布，后台将在数秒内完成规则匹配";
        }
        catch (JsonException)
        {
            TempData["ErrorMessage"] = "Payload 必须是合法 JSON";
        }
        return RedirectToPage();
    }

    public string StatusLabel(AgentEventExecutionStatus status) => status switch
    {
        AgentEventExecutionStatus.Pending => "待执行",
        AgentEventExecutionStatus.Running => "执行中",
        AgentEventExecutionStatus.WaitingApproval => "等待审批",
        AgentEventExecutionStatus.Retrying => "等待重试",
        AgentEventExecutionStatus.Completed => "已完成",
        AgentEventExecutionStatus.Failed => "失败",
        AgentEventExecutionStatus.Skipped => "已跳过",
        AgentEventExecutionStatus.Cancelled => "已取消",
        _ => status.ToString()
    };

    public string StatusClass(AgentEventExecutionStatus status) => status switch
    {
        AgentEventExecutionStatus.Running => "primary",
        AgentEventExecutionStatus.WaitingApproval or AgentEventExecutionStatus.Retrying => "warning text-dark",
        AgentEventExecutionStatus.Completed => "success",
        AgentEventExecutionStatus.Failed => "danger",
        AgentEventExecutionStatus.Skipped => "secondary",
        _ => "info text-dark"
    };

    public string GetConditionOperatorLabel(AgentEventConditionOperator value) => value switch
    {
        AgentEventConditionOperator.Equals => "等于",
        AgentEventConditionOperator.NotEquals => "不等于",
        AgentEventConditionOperator.Contains => "包含",
        AgentEventConditionOperator.StartsWith => "开头是",
        AgentEventConditionOperator.GreaterThan => "数值大于",
        AgentEventConditionOperator.LessThan => "数值小于",
        AgentEventConditionOperator.Exists => "字段存在",
        _ => "无条件"
    };

    private async Task LoadAsync()
    {
        Messages = await _eventBus.GetRecentAsync(50);
        Rules = await _context.AgentEventSubscriptions.AsNoTracking()
            .Include(item => item.AgentDefinition)
            .Include(item => item.Project)
            .Include(item => item.CreatedByUser)
            .OrderByDescending(item => item.IsEnabled)
            .ThenBy(item => item.EventType)
            .ToListAsync();
        Executions = await _context.AgentEventExecutions.AsNoTracking()
            .Include(item => item.Subscription)
            .Include(item => item.AgentDefinition)
            .Include(item => item.Project)
            .Include(item => item.Task)
            .Include(item => item.WaitingApprovalRequest)
            .OrderByDescending(item => item.CreatedAt)
            .Take(50)
            .ToListAsync();
        Agents = await _context.AgentDefinitions.AsNoTracking()
            .Where(item => item.IsEnabled)
            .OrderBy(item => item.Name)
            .ToListAsync();
        Projects = await _context.Project.AsNoTracking()
            .Where(item => !item.IsDeleted)
            .OrderBy(item => item.Name)
            .ToListAsync();
    }

    private static AgentEventSubscription NewRule() => new()
    {
        IsEnabled = true,
        MaxAttempts = 3,
        MaxSteps = 5,
        DailyExecutionLimit = 50,
        MaxTokenBudget = 20000,
        PromptTemplate = "收到系统事件 {eventType}。请结合当前项目和任务上下文分析事件，给出可执行建议。\n事件对象：{aggregateType}/{aggregateId}\n事件数据：{payload}"
    };
}

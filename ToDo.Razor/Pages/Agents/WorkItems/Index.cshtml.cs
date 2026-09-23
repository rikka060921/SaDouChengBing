using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Agents.WorkItems;

[Authorize(Roles = "systemAdmin")]
public class IndexModel : PageModel
{
    private const int PageSize = 30;
    private readonly ApplicationDbContext _context;
    private readonly AgentWorkQueueService _queue;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        ApplicationDbContext context,
        AgentWorkQueueService queue,
        UserManager<ApplicationUser> userManager,
        ILogger<IndexModel> logger)
    {
        _context = context;
        _queue = queue;
        _userManager = userManager;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)] public AgentWorkItemStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public int? ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public int? AgentId { get; set; }
    [BindProperty(SupportsGet = true)] public string Keyword { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

    public List<AgentWorkItem> Items { get; private set; } = [];
    public List<Project> Projects { get; private set; } = [];
    public List<AgentDefinition> Agents { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public int ActiveCount { get; private set; }
    public int WaitingApprovalCount { get; private set; }
    public int FailedCount { get; private set; }

    public async Task OnGetAsync()
    {
        PageNumber = Math.Max(1, PageNumber);
        var all = _context.AgentWorkItems.AsNoTracking();
        ActiveCount = await all.CountAsync(item => item.Status == AgentWorkItemStatus.Pending
            || item.Status == AgentWorkItemStatus.Running
            || item.Status == AgentWorkItemStatus.Retrying
            || item.Status == AgentWorkItemStatus.Paused
            || item.Status == AgentWorkItemStatus.WaitingPlanConfirmation);
        WaitingApprovalCount = await all.CountAsync(item => item.Status == AgentWorkItemStatus.WaitingApproval);
        FailedCount = await all.CountAsync(item => item.Status == AgentWorkItemStatus.Failed);

        var query = all.AsQueryable();
        if (Status.HasValue) query = query.Where(item => item.Status == Status.Value);
        if (ProjectId.HasValue) query = query.Where(item => item.ProjectId == ProjectId.Value);
        if (AgentId.HasValue) query = query.Where(item => item.AgentDefinitionId == AgentId.Value);
        var keyword = Keyword.Trim();
        if (keyword.Length > 0)
        {
            query = query.Where(item => item.Task != null && item.Task.Title.Contains(keyword)
                || item.AgentDefinition != null && item.AgentDefinition.Name.Contains(keyword)
                || item.IdempotencyKey.Contains(keyword)
                || item.CauseChainId.Contains(keyword));
        }

        TotalCount = await query.CountAsync();
        if (PageNumber > TotalPages) PageNumber = TotalPages;
        Items = await query
            .Include(item => item.AgentDefinition)
            .Include(item => item.Task)
            .Include(item => item.Project)
            .Include(item => item.RequestedByUser)
            .Include(item => item.WaitingApprovalRequest)
            .Include(item => item.DeliveryReceipt)
            .OrderByDescending(item => item.CreatedAt)
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();
        Projects = await _context.Project.AsNoTracking().Where(item => !item.IsDeleted).OrderBy(item => item.Name).ToListAsync();
        Agents = await _context.AgentDefinitions.AsNoTracking().OrderBy(item => item.Name).ToListAsync();
    }

    public Task<IActionResult> OnPostPauseAsync(int id) => ExecuteAsync(id, "暂停", _queue.PauseAsync);
    public Task<IActionResult> OnPostResumeAsync(int id) => ExecuteAsync(id, "恢复", _queue.ResumeAsync);
    public Task<IActionResult> OnPostCancelAsync(int id) => ExecuteAsync(id, "取消", _queue.CancelAsync);

    public async Task<IActionResult> OnPostRetryAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        try
        {
            var retried = await _queue.RetryAsync(id, user.Id);
            TempData["SuccessMessage"] = $"已创建重试工作项 #{retried.Id}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重试 Agent 工作项失败，WorkItemId={WorkItemId}", id);
            TempData["ErrorMessage"] = "重试工作项失败，请稍后重试";
        }
        return RedirectToFilters();
    }

    public string GetStatusLabel(AgentWorkItemStatus status) => status switch
    {
        AgentWorkItemStatus.WaitingPlanConfirmation => "待确认执行计划",
        AgentWorkItemStatus.Pending => "待执行",
        AgentWorkItemStatus.Running => "执行中",
        AgentWorkItemStatus.WaitingApproval => "等待审批",
        AgentWorkItemStatus.Retrying => "等待重试",
        AgentWorkItemStatus.Completed => "已完成",
        AgentWorkItemStatus.Failed => "失败",
        AgentWorkItemStatus.Cancelled => "已取消",
        AgentWorkItemStatus.Paused => "已暂停",
        _ => status.ToString()
    };

    public string GetStatusClass(AgentWorkItemStatus status) => status switch
    {
        AgentWorkItemStatus.Running => "primary",
        AgentWorkItemStatus.WaitingApproval or AgentWorkItemStatus.Retrying => "warning text-dark",
        AgentWorkItemStatus.Completed => "success",
        AgentWorkItemStatus.Failed => "danger",
        AgentWorkItemStatus.Paused => "info text-dark",
        _ => "secondary"
    };

    public string GetTriggerLabel(AgentWorkTriggerType trigger) => trigger switch
    {
        AgentWorkTriggerType.TaskAssigned => "任务指派",
        AgentWorkTriggerType.TaskCommentAdded => "新增评论",
        AgentWorkTriggerType.TaskReviewRejected => "审核驳回",
        AgentWorkTriggerType.ApprovalResolved => "审批完成",
        AgentWorkTriggerType.ManualRetry => "手动重试",
        _ => trigger.ToString()
    };

    private async Task<IActionResult> ExecuteAsync(
        int id,
        string actionName,
        Func<int, int, CancellationToken, Task> action)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        try
        {
            await action(id, user.Id, HttpContext.RequestAborted);
            TempData["SuccessMessage"] = $"工作项 #{id} 已{actionName}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent 工作项操作失败，WorkItemId={WorkItemId}, Action={Action}", id, actionName);
            TempData["ErrorMessage"] = "工作项操作失败，请稍后重试";
        }
        return RedirectToFilters();
    }

    private IActionResult RedirectToFilters()
    {
        return RedirectToPage(new
        {
            status = Status,
            projectId = ProjectId,
            agentId = AgentId,
            keyword = Keyword,
            pageNumber = PageNumber
        });
    }
}

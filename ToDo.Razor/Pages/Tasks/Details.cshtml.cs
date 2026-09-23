using Markdig;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;
using ToDoTaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Razor.Pages.Tasks;

public class DetailsModel : PageModel
{
    private readonly ApplicationDbContext _dbcontext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ToDoTaskDomainService _taskDomainService;
    private readonly UserNotificationService _notifications;
    private readonly AgentWorkQueueService _agentWorkQueue;
    private readonly AgentDispatchService _agentDispatch;
    private readonly AgentOutcomeService _agentOutcomes;
    private readonly IEventBus _eventBus;
    private readonly ILogger<DetailsModel> _logger;
    private readonly AgentPlanningService _planning;
    private readonly TaskAssistanceService? _assistance;

    public DetailsModel(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        ToDoTaskDomainService taskDomainService,
        UserNotificationService notifications,
        AgentWorkQueueService agentWorkQueue,
        AgentDispatchService agentDispatch,
        AgentOutcomeService agentOutcomes,
        IEventBus eventBus,
        ILogger<DetailsModel> logger,
        AgentPlanningService planning,
        TaskAssistanceService? assistance = null)
    {
        _dbcontext = context;
        _userManager = userManager;
        _taskDomainService = taskDomainService;
        _notifications = notifications;
        _agentWorkQueue = agentWorkQueue;
        _agentDispatch = agentDispatch;
        _agentOutcomes = agentOutcomes;
        _eventBus = eventBus;
        _logger = logger;
        _planning = planning;
        _assistance = assistance;
    }

    public ToDoTask TaskDetail { get; set; } = default!;
    public bool ProjectRole { get; set; }
    public bool CanEdit { get; set; }
    public bool CanReview { get; set; }
    public bool CanUpdateProgress { get; set; }
    [BindProperty] public int ExecutionProgress { get; set; }
    [BindProperty] public int ExpectedTaskVersion { get; set; }
    [BindProperty] public string? ProgressNote { get; set; }
    public bool CanReviewPlan { get; set; }
    public AgentWorkItem? PendingPlan { get; set; }
    [BindProperty] public string PlanFeedback { get; set; } = string.Empty;
    public List<TaskComment> Comments { get; set; } = new();
    public List<ToDoTask> SubTasks { get; set; } = new();
    public List<TaskLabel> Labels { get; set; } = new();
    public AgentWorkItem? LatestAgentWorkItem { get; set; }
    public List<AgentWorkItem> AgentWorkItems { get; set; } = [];
    public AgentDispatchDecision? LatestDispatchDecision { get; set; }
    public IReadOnlyList<AgentDispatchCandidate> DispatchCandidates { get; set; } = [];
    public AgentDeliveryReceipt? LatestDeliveryReceipt { get; set; }
    public IReadOnlyList<AgentDeliveryEvidence> DeliveryEvidence { get; set; } = [];
    public IReadOnlyList<AgentToolEffect> DeliveryToolEffects { get; set; } = [];
    public bool CanUseTaskAssistance { get; set; }
    public AgentRunJob? MyTaskAssistance { get; set; }
    public Guid AssistanceRequestId { get; set; } = Guid.NewGuid();
    public HashSet<int> AccessibleSessionIds { get; set; } = [];
    public bool CanViewSession(int? sessionId) => sessionId.HasValue && AccessibleSessionIds.Contains(sessionId.Value);

    [BindProperty] public string CommentText { get; set; } = string.Empty;
    [BindProperty] public string ReviewComment { get; set; } = string.Empty;
    [BindProperty] public int SelectedDispatchAgentId { get; set; }

    public int? TaskDetailsCurrentUserId => int.TryParse(_userManager.GetUserId(User), out var id) ? id : null;
    public string DescriptionHtml => Markdown.ToHtml(
        TaskDetail.Description ?? string.Empty,
        new MarkdownPipelineBuilder().DisableHtml().UseAdvancedExtensions().Build());

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Response.Headers.CacheControl = "no-store";
        var task = await _taskDomainService.GetTaskByIdAsync(id);
        if (task == null || task.IsDeleted) return NotFound();
        TaskDetail = task;
        var userId = TaskDetailsCurrentUserId;
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return Unauthorized();
        if (currentUser.IsDeleted || currentUser.Status != UserStatus.Active) return Forbid();
        if (TaskDetail.Project == null || TaskDetail.Project.IsDeleted) return NotFound();
        var canAccessProject = currentUser.Role == UserRole.systemAdmin
            || TaskDetail.Project?.LeaderUserId == currentUser.Id
            || await _dbcontext.ProjectUsers.AnyAsync(item => item.ProjectId == TaskDetail.ProjectId && item.UserId == currentUser.Id);
        if (!canAccessProject) return Forbid();
        ProjectRole = userId.HasValue && await _dbcontext.ProjectUsers.AnyAsync(item => item.ProjectId == TaskDetail.ProjectId && item.UserId == userId && item.ProjectRole == 0);
        var isProjectLeader = userId.HasValue && TaskDetail.Project?.LeaderUserId == userId.Value;
        var isSystemAdmin = currentUser.Role == UserRole.systemAdmin;
        CanEdit = TaskDetail.Project?.Status == ProjectStatus.Active && (ProjectRole || isProjectLeader || isSystemAdmin || (userId.HasValue && TaskDetail.CreatorId == userId));
        CanReview = TaskDetail.Project?.Status == ProjectStatus.Active && TaskReviewService.CanReviewTask(TaskDetail, currentUser, ProjectRole || isProjectLeader || isSystemAdmin);
        CanUpdateProgress = await new TaskProgressService(_dbcontext).CanUpdateAsync(id, currentUser.Id, HttpContext.RequestAborted);
        ExecutionProgress = TaskDetail.Progress;
        ExpectedTaskVersion = TaskDetail.ConcurrencyVersion;
        Comments = TaskDetail.Comments.OrderByDescending(item => item.CreatedAt).ToList();
        SubTasks = TaskDetail.SubTasks.OrderBy(item => item.CreatedAt).ToList();
        Labels = TaskDetail.LabelLinks.Where(item => item.Label != null && !item.Label.IsDeleted).Select(item => item.Label!).ToList();
        AgentWorkItems = await _dbcontext.AgentWorkItems.AsNoTracking()
            .Include(item => item.AgentDefinition)
            .Include(item => item.WaitingApprovalRequest)
            .Where(item => item.TaskId == id)
            .OrderByDescending(item => item.CreatedAt)
            .Take(10)
            .ToListAsync();
        LatestAgentWorkItem = AgentWorkItems.FirstOrDefault();
        PendingPlan = await _dbcontext.AgentWorkItems.AsNoTracking()
            .Where(item => item.TaskId == id && item.Status == AgentWorkItemStatus.WaitingPlanConfirmation
                && item.AgentDefinitionId == task.AgentDefinitionId)
            .OrderBy(item => item.Id).FirstOrDefaultAsync();
        CanReviewPlan = await _planning.CanReviewAsync(task, currentUser.Id, HttpContext.RequestAborted);
        LatestDispatchDecision = await _agentDispatch.GetLatestAsync(id);
        DispatchCandidates = AgentDispatchService.ParseCandidates(LatestDispatchDecision?.CandidatesJson);
        LatestDeliveryReceipt = await _dbcontext.AgentDeliveryReceipts.AsNoTracking()
            .Include(item => item.AgentDefinition)
            .Include(item => item.ReviewedByUser)
            .Where(item => item.TaskId == id)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync();
        DeliveryEvidence = AgentOutcomeService.ParseEvidence(LatestDeliveryReceipt?.EvidenceJson);
        DeliveryToolEffects = AgentOutcomeService.ParseToolEffects(LatestDeliveryReceipt?.ToolEffectsJson);
        var sessionIds = AgentWorkItems.Where(w => w.AiSessionId.HasValue).Select(w => w.AiSessionId!.Value)
            .Concat(Comments.Where(c => c.AiSessionId.HasValue).Select(c => c.AiSessionId!.Value)).Distinct().ToArray();
        AccessibleSessionIds = (await _dbcontext.AiSessions.AsNoTracking()
            .Where(s => sessionIds.Contains(s.Id) && (isSystemAdmin || s.UserId == currentUser.Id))
            .Select(s => s.Id).ToListAsync(HttpContext.RequestAborted)).ToHashSet();
        if (_assistance != null)
        {
            CanUseTaskAssistance = await _assistance.CanAssistAsync(id, currentUser, HttpContext.RequestAborted);
            MyTaskAssistance = await _assistance.GetLatestAsync(id, currentUser, HttpContext.RequestAborted);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostProgressAsync(int id, bool submitForReview)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();
        try
        {
            var progressFields = new[] { nameof(ExecutionProgress), nameof(ExpectedTaskVersion), nameof(ProgressNote), nameof(submitForReview) };
            foreach (var key in ModelState.Keys.Where(key => !progressFields.Contains(key, StringComparer.OrdinalIgnoreCase)).ToArray())
                ModelState.Remove(key);
            if (!ModelState.IsValid) throw new InvalidOperationException("进度格式有误，请输入 0—99 的整数。");
            await new TaskProgressService(_dbcontext).SaveAsync(id, user.Id, ExpectedTaskVersion,
                ExecutionProgress, ProgressNote, submitForReview, HttpContext.RequestAborted);
            TempData["TaskMessage"] = submitForReview ? "成果已提交，已提醒审核人；你无需另外催办。" : "执行进展已保存，不会额外打扰其他成员。";
            return RedirectToPage("./Details", new { id });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException)
        {
            ModelState.AddModelError(string.Empty, "任务已被其他操作更新；请先复制下方保留的说明，再重新打开任务查看最新进展。");
        }
        catch (InvalidOperationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "成员更新任务进展失败，TaskId={TaskId}", id);
            ModelState.AddModelError(string.Empty, "保存失败，请稍后重试；本次输入保留在下方。");
        }
        // 不用重定向丢掉成员刚写的成果说明，也不能把冲突版本静默替换成新版本。
        var submittedProgress = ExecutionProgress;
        var submittedVersion = ExpectedTaskVersion;
        var submittedNote = ProgressNote;
        _dbcontext.ChangeTracker.Clear();
        var result = await OnGetAsync(id);
        ExecutionProgress = submittedProgress;
        ExpectedTaskVersion = submittedVersion;
        ProgressNote = submittedNote;
        return result;
    }

    public async Task<IActionResult> OnPostAssistAsync(int id, string purpose, Guid requestId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();
        if (_assistance == null) return StatusCode(503);
        try
        {
            await _assistance.RequestAsync(id, purpose, user, requestId, HttpContext.RequestAborted);
            TempData["TaskMessage"] = "AI 已收到请求，你可以继续工作，结果会显示在本页。任务负责人和进度不会改变。";
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            TempData["TaskMessage"] = ex.Message;
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "任务内 AI 协助请求失败，TaskId={TaskId}, UserId={UserId}", id, user.Id);
            TempData["TaskMessage"] = "AI 协助暂时无法启动，请稍后重试；任务内容没有被修改。";
        }
        return RedirectToPage("./Details", null, new { id }, "task-assistance");
    }

    public async Task<IActionResult> OnGetAssistanceStatusAsync(int id)
    {
        Response.Headers.CacheControl = "no-store";
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();
        if (_assistance == null) return StatusCode(503);
        if (user.IsDeleted || user.Status != UserStatus.Active) return Forbid();
        var canRequest = await _assistance.CanAssistAsync(id, user, HttpContext.RequestAborted);
        var job = await _assistance.GetLatestAsync(id, user, HttpContext.RequestAborted);
        if (!canRequest && job == null) return NotFound();
        return new JsonResult(new
        {
            hasResult = job != null,
            jobId = job?.Id,
            status = job?.Status.ToString() ?? "None",
            statusLabel = job == null ? "还没有请求协助" : GetAssistanceStatusLabel(job.Status),
            isActive = job != null && IsAssistanceActive(job.Status),
            result = job?.Status == AgentRunJobStatus.Completed ? job.ResultSummary : string.Empty,
            error = GetAssistanceError(job),
            updatedAtLabel = job?.UpdatedAt.ToString("MM-dd HH:mm") ?? string.Empty,
            sessionUrl = job == null ? null : Url.Page("/AiSessions/Details", new { id = job.AiSessionId }),
            requestId = Guid.NewGuid().ToString(),
            canRequest
        });
    }

    public static bool IsAssistanceActive(AgentRunJobStatus status) =>
        status is AgentRunJobStatus.Pending or AgentRunJobStatus.Running or AgentRunJobStatus.Retrying or AgentRunJobStatus.WaitingApproval;

    public static string GetAssistanceStatusLabel(AgentRunJobStatus status) => status switch
    {
        AgentRunJobStatus.Pending => "已排队，稍后为你处理",
        AgentRunJobStatus.Running => "正在整理建议",
        AgentRunJobStatus.Retrying => "后台正在自动重试",
        AgentRunJobStatus.Completed => "建议已准备好",
        AgentRunJobStatus.Cancelled => "本次协助已取消",
        AgentRunJobStatus.WaitingApproval => "执行暂时受阻",
        _ => "本次协助未完成"
    };

    public static string GetAssistanceError(AgentRunJob? job) => job?.Status switch
    {
        AgentRunJobStatus.Failed => "本次协助暂未完成，可以重新请求；如持续失败，请联系项目管理员。任务内容没有被修改。",
        AgentRunJobStatus.Retrying => "你无需重复提交，后台会继续尝试。",
        AgentRunJobStatus.WaitingApproval => "只读协助不应要求写入审批，请联系项目管理员检查此执行。",
        _ => string.Empty
    };

    public async Task<IActionResult> OnPostReviewPlanAsync(int id, int workItemId, int revision, bool approved)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();
        try
        {
            await _planning.ReviewAsync(id, workItemId, revision, user.Id, approved, PlanFeedback, HttpContext.RequestAborted);
            TempData["TaskMessage"] = approved ? "计划已确认，Agent 将按计划执行；敏感操作仍需单独审批" : "调整意见已保存，Agent 将重新生成计划";
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { TempData["TaskMessage"] = ex.Message; }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostConfirmDispatchAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();
        var decision = await _agentDispatch.GetLatestAsync(id);
        if (decision == null) return NotFound();
        try
        {
            await _agentDispatch.ConfirmAsync(decision.Id, SelectedDispatchAgentId, user.Id);
            TempData["TaskMessage"] = "Agent 派单已确认，将自动规划、执行并检查交付；需要你介入时会提示";
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            TempData["TaskMessage"] = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to confirm dispatch for task {TaskId}", id);
            TempData["TaskMessage"] = "Agent 派单确认失败，请稍后重试";
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddCommentAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        var task = await _dbcontext.ToDoTasks.FirstOrDefaultAsync(item => item.Id == id && !item.IsDeleted);
        if (user == null || task == null) return Forbid();
        var canAccessProject = user.Role == UserRole.systemAdmin
            || await _dbcontext.Project.AnyAsync(item => item.Id == task.ProjectId && item.LeaderUserId == user.Id)
            || await _dbcontext.ProjectUsers.AnyAsync(item => item.ProjectId == task.ProjectId && item.UserId == user.Id);
        if (!canAccessProject) return Forbid();
        if (string.IsNullOrWhiteSpace(CommentText)) { TempData["TaskMessage"] = "评论内容不能为空"; return RedirectToPage(new { id }); }
        var comment = new TaskComment { TaskId = id, AuthorId = user.Id, Content = CommentText.Trim() };
        _dbcontext.TaskComments.Add(comment);
        await _dbcontext.SaveChangesAsync();
        if (task.AssigneeType == TaskAssigneeType.DigitalEmployee && task.AgentDefinitionId.HasValue)
        {
            await _agentWorkQueue.EnqueueTaskAsync(
                task.Id,
                user.Id,
                AgentWorkTriggerType.TaskCommentAdded,
                comment.Id,
                $"任务收到新的人工评论，请结合上下文继续执行并回应：\n{comment.Content}",
                $"task-comment:{task.Id}:{comment.Id}:{task.AgentDefinitionId}");
        }
        var ids = await StakeholdersAsync(task);
        ids.Remove(user.Id);
        await _notifications.NotifyManyAsync(ids, "任务新增评论", $"任务「{task.Title}」收到一条新评论", "Task", $"/Tasks/Details/{id}");
        await _eventBus.PublishAsync(
            "task.comment-added",
            new { taskId = task.Id, projectId = task.ProjectId, commentId = comment.Id, authorId = user.Id, comment.Content },
            "Task",
            task.Id.ToString());
        TempData["TaskMessage"] = "评论已发布";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostReviewAsync(int id, bool approved)
    {
        var user = await _userManager.GetUserAsync(User);
        var task = await _dbcontext.ToDoTasks.FirstOrDefaultAsync(item => item.Id == id && !item.IsDeleted);
        if (user == null || task == null) return Forbid();
        var project = await _dbcontext.Project.AsNoTracking().FirstOrDefaultAsync(item => item.Id == task.ProjectId);
        var canReview = await new TaskReviewService(_dbcontext).CanReviewAsync(task, user);
        if (!canReview) return Forbid();
        var isPrivilegedReviewer = user.Role == UserRole.systemAdmin
            || project?.LeaderUserId == user.Id
            || await _dbcontext.ProjectUsers.AnyAsync(item => item.ProjectId == task.ProjectId && item.UserId == user.Id && item.ProjectRole == 0);
        if (task.CreatorId == user.Id && !isPrivilegedReviewer)
        {
            TempData["TaskMessage"] = "任务创建者不能审核自己创建的任务，请由项目管理员或其他审核人处理";
            return RedirectToPage(new { id });
        }
        if (task.Status != ToDoTaskStatus.PendingConfirmation) { TempData["TaskMessage"] = "任务当前不在待审核状态"; return RedirectToPage(new { id }); }
        var before = task.Status;
        task.ApplyReviewDecision(approved);
        task.UpdatedAt = AppTime.Now;
        var reviewEntry = new TaskComment { TaskId = id, AuthorId = user.Id, Content = string.IsNullOrWhiteSpace(ReviewComment) ? (approved ? "审核通过" : "审核驳回，需返工") : ReviewComment.Trim() };
        _dbcontext.TaskComments.Add(reviewEntry);
        await _agentOutcomes.ApplyHumanReviewAsync(task.Id, approved, user.Id, reviewEntry.Content);
        await _dbcontext.SaveChangesAsync();
        if (approved)
        {
            await _agentWorkQueue.CancelPendingForTaskAsync(task.Id);

            // 任务审核通过（进入已完成）→ 自动归档该任务关联的活跃议题
            try
            {
                var now = AppTime.Now;
                var activeAgendas = await _dbcontext.MeetingAgendas
                    .Where(a => a.SourceId == task.Id && a.Status == AgendaStatus.Active && !a.IsDeleted)
                    .ToListAsync();
                foreach (var agenda in activeAgendas)
                {
                    agenda.Status = AgendaStatus.Archived;
                    agenda.ArchivedAt = now;
                    agenda.ArchivedByMeetingId = null; // 任务审核通过触发，无关联会议
                    agenda.LastModifiedAt = now;
                }
                await _dbcontext.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "任务 {TaskId} 审核通过后自动归档议题失败", task.Id);
            }
        }
        else if (task.AssigneeType == TaskAssigneeType.DigitalEmployee && task.AgentDefinitionId.HasValue)
        {
            await _agentWorkQueue.EnqueueTaskAsync(
                task.Id,
                user.Id,
                AgentWorkTriggerType.TaskReviewRejected,
                reviewEntry.Id,
                $"任务审核被打回，请根据审核意见返工并重新提交可核验结果：\n{reviewEntry.Content}",
                $"task-review-rejected:{task.Id}:{reviewEntry.Id}:{task.AgentDefinitionId}");
        }
        await _taskDomainService.LogTaskOperationAsync(approved ? OperationType.状态变更 : OperationType.更新, OperationTarget.任务, user.Id, projectId: task.ProjectId, taskId: id, taskTitle: task.Title, targetId: id, beforeState: $"状态：{before}", afterState: $"状态：{task.Status}\n审核意见：{ReviewComment}", status: OperationStatus.成功);
        var ids = approved ? (await StakeholdersAsync(task)).ToList()
            : await new AttentionRecipientService(_dbcontext).TaskAsync(task, task.ReworkCount >= 2);
        ids.Remove(user.Id);
        var quietRework = !approved && task.AssigneeType == TaskAssigneeType.DigitalEmployee && task.ReworkCount < 2;
        await _notifications.NotifyManyAsync(ids, approved ? "任务审核通过" : "任务被打回返工",
            approved ? $"任务「{task.Title}」已审核通过" : $"任务「{task.Title}」被打回，累计返工 {task.ReworkCount} 次。{ReviewComment}",
            approved ? "Success" : quietRework ? "Task" : "Warning", $"/Tasks/Details/{id}");
        await _eventBus.PublishAsync(
            "task.reviewed",
            new
            {
                taskId = task.Id,
                projectId = task.ProjectId,
                approved,
                reviewerId = user.Id,
                task.Status,
                task.ReworkCount,
                comment = reviewEntry.Content
            },
            "Task",
            task.Id.ToString());
        TempData["TaskMessage"] = approved ? "任务已审核通过" : "任务已打回返工";
        return RedirectToPage(new { id });
    }

    private async Task<HashSet<int>> StakeholdersAsync(ToDoTask task)
    {
        var ids = new HashSet<int> { task.CreatorId };
        if (task.AssigneeId.HasValue) ids.Add(task.AssigneeId.Value);
        if (task.ReviewerId.HasValue) ids.Add(task.ReviewerId.Value);
        foreach (var id in await _dbcontext.ProjectUsers.Where(item => item.ProjectId == task.ProjectId && item.ProjectRole == 0).Select(item => item.UserId).ToListAsync()) ids.Add(id);
        return ids;
    }

    public string FormatContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "无数据";
        try { return JsonSerializer.Serialize(JsonDocument.Parse(content).RootElement, new JsonSerializerOptions { WriteIndented = true }); }
        catch { return content; }
    }

    public DateTime ConvertToLocalTime(DateTime utcTime) => AppTime.ToBeijingTime(utcTime);

    public static string GetAgentWorkStatusLabel(AgentWorkItemStatus status) => status switch
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

    public static string GetAgentWorkStatusClass(AgentWorkItemStatus status) => status switch
    {
        AgentWorkItemStatus.Running => "primary",
        AgentWorkItemStatus.WaitingApproval or AgentWorkItemStatus.Retrying => "warning text-dark",
        AgentWorkItemStatus.Completed => "success",
        AgentWorkItemStatus.Failed => "danger",
        AgentWorkItemStatus.Paused => "info text-dark",
        _ => "secondary"
    };

    public static string GetAgentWorkTriggerLabel(AgentWorkTriggerType trigger) => trigger switch
    {
        AgentWorkTriggerType.TaskAssigned => "自动派单",
        AgentWorkTriggerType.TaskCommentAdded => "人工补充信息",
        AgentWorkTriggerType.TaskReviewRejected => "审核返工",
        AgentWorkTriggerType.ApprovalResolved => "审批后继续",
        AgentWorkTriggerType.ManualRetry => "人工重试",
        _ => trigger.ToString()
    };

    public static bool IsAgentWorkItemStalled(AgentWorkItem item) =>
        item.Status == AgentWorkItemStatus.Running
        && item.LockedAt.HasValue
        && item.LockedAt.Value < AppTime.Now.Subtract(AgentWorkQueueService.StaleWorkItemTimeout);

}

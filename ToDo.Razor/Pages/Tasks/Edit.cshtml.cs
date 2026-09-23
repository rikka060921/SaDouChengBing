using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;
using TodoTask = ToDo.Entities.ToDoTask;
using ToDoTaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Razor.Pages.Tasks;

public class EditModel : PageModel
{
    private readonly ApplicationDbContext _dbcontext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ToDoTaskDomainService _taskDomainService;
    private readonly UserNotificationService _notifications;
    private readonly AgentWorkQueueService _agentWorkQueue;
    private readonly AgentDispatchService _agentDispatch;
    private readonly IEventBus _eventBus;

    public EditModel(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        ToDoTaskDomainService taskDomainService,
        UserNotificationService notifications,
        AgentWorkQueueService agentWorkQueue,
        AgentDispatchService agentDispatch,
        IEventBus eventBus)
    {
        _dbcontext = context;
        _userManager = userManager;
        _taskDomainService = taskDomainService;
        _notifications = notifications;
        _agentWorkQueue = agentWorkQueue;
        _agentDispatch = agentDispatch;
        _eventBus = eventBus;
    }

    [BindProperty]
    public TodoTask Task { get; set; } = new() { CreatorId = 0 };

    [BindProperty(SupportsGet = true)]
    public string? ReturnPage { get; set; } = "Index";

    [BindProperty(SupportsGet = true)]
    public int? ReturnId { get; set; }

    [BindProperty(SupportsGet = true)]
    public int? Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public int ProjectId { get; set; }

    [BindProperty]
    public bool IsEditingExisting { get; set; }
    // 加上?允许null；加上SupportsGet=false，关键：设置BindProperty不强制要求表单必须存在此字段
    [BindProperty(SupportsGet = false)]
    public string? LabelNames { get; set; } = string.Empty;


    public SelectList GroupList { get; set; } = default!;
    public SelectList UserList { get; set; } = default!;
    public SelectList ReviewerList { get; set; } = default!;
    public SelectList ParentTaskList { get; set; } = default!;

    private int? CurrentUserId => int.TryParse(_userManager.GetUserId(User), out var id) ? id : null;

    public async Task<IActionResult> OnGetAsync(int? projectId, int? id)
    {
        if (!projectId.HasValue) return NotFound();
        if (!CurrentUserId.HasValue) return Unauthorized();
        ProjectId = projectId.Value;
        await LoadSelectListsAsync(ProjectId, id);

        if (!id.HasValue)
        {
            if (!await CanEditTaskAsync(ProjectId, CurrentUserId.Value, null)) return Forbid();
            var projectLeaderId = await _dbcontext.Project.AsNoTracking()
                .Where(item => item.Id == ProjectId && !item.IsDeleted)
                .Select(item => item.LeaderUserId)
                .FirstOrDefaultAsync();
            Task = new TodoTask { ProjectId = ProjectId, CreatorId = CurrentUserId.Value, ReviewerId = projectLeaderId };
            return Page();
        }

        IsEditingExisting = true;
        var existingTask = await _dbcontext.ToDoTasks
            .Include(item => item.Group)
            .Include(item => item.LabelLinks).ThenInclude(link => link.Label)
            .FirstOrDefaultAsync(item => item.Id == id.Value);
        if (existingTask == null) return NotFound();
        Task = existingTask;
        if (!await CanEditTaskAsync(Task.ProjectId, CurrentUserId.Value, Task.Id)) return Forbid();

        LabelNames = string.Join(", ", Task.LabelLinks
            .Where(link => link.Label != null && !link.Label.IsDeleted)
            .Select(link => link.Label!.Name));
        ProjectId = Task.ProjectId;
        await LoadSelectListsAsync(ProjectId, Task.Id);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        int? dispatchConfirmationTaskId = null;
        var currentUserId = CurrentUserId;
        if (!currentUserId.HasValue) return Unauthorized();
        var actualProjectId = Task.Id == 0
            ? ProjectId
            : await _dbcontext.ToDoTasks.AsNoTracking()
                .Where(item => item.Id == Task.Id && !item.IsDeleted)
                .Select(item => item.ProjectId)
                .FirstOrDefaultAsync();
        if (actualProjectId <= 0) return NotFound();
        if (!await CanEditTaskAsync(actualProjectId, currentUserId.Value, Task.Id == 0 ? null : Task.Id)) return Forbid();
        ProjectId = actualProjectId;

        if (Task.AssigneeType == TaskAssigneeType.DigitalEmployee)
        {
            Task.AssigneeId = null;
            // 具体 Agent 由调度器选择，不信任客户端提交的 AgentDefinitionId。
            Task.AgentDefinitionId = null;
            Task.AgentName = null;
        }
        else
        {
            Task.AgentDefinitionId = null;
            Task.AgentName = null;
        }

        if (!ModelState.IsValid)
        {
            await LoadSelectListsAsync(ProjectId, Task.Id == 0 ? null : Task.Id);
            return Page();
        }

        if (Task.ParentTaskId == Task.Id && Task.Id != 0)
        {
            ModelState.AddModelError("Task.ParentTaskId", "父任务不能选择自己");
            await LoadSelectListsAsync(ProjectId, Task.Id);
            return Page();
        }

        if (Task.ParentTaskId.HasValue && !await _dbcontext.ToDoTasks.AnyAsync(item =>
            item.Id == Task.ParentTaskId.Value && item.ProjectId == ProjectId && !item.IsDeleted))
        {
            ModelState.AddModelError("Task.ParentTaskId", "父任务必须属于当前项目");
            await LoadSelectListsAsync(ProjectId, Task.Id);
            return Page();
        }

        var validProjectUserIds = await _dbcontext.ProjectUsers.AsNoTracking()
            .Where(member => member.ProjectId == ProjectId)
            .Select(member => member.UserId)
            .ToListAsync();
        var projectLeaderId = await _dbcontext.Project.AsNoTracking()
            .Where(item => item.Id == ProjectId && !item.IsDeleted)
            .Select(item => item.LeaderUserId)
            .FirstOrDefaultAsync();
        if (projectLeaderId > 0 && !validProjectUserIds.Contains(projectLeaderId))
            validProjectUserIds.Add(projectLeaderId);
        if (Task.AssigneeId.HasValue && !validProjectUserIds.Contains(Task.AssigneeId.Value))
        {
            ModelState.AddModelError("Task.AssigneeId", "负责人必须是当前项目成员");
        }
        if (Task.ReviewerId.HasValue && !validProjectUserIds.Contains(Task.ReviewerId.Value))
        {
            ModelState.AddModelError("Task.ReviewerId", "审核人必须是当前项目成员");
        }
        if (!ModelState.IsValid)
        {
            await LoadSelectListsAsync(ProjectId, Task.Id == 0 ? null : Task.Id);
            return Page();
        }

        var isProjectAdmin = await _dbcontext.ProjectUsers.AnyAsync(item => item.ProjectId == ProjectId && item.UserId == currentUserId.Value && item.ProjectRole == 0);
        var isProjectLeader = projectLeaderId == currentUserId.Value;
        if (Task.Status == ToDoTaskStatus.Completed && !isProjectAdmin && !isProjectLeader && !User.IsInRole("systemAdmin"))
            Task.Status = ToDoTaskStatus.PendingConfirmation;
        Task.SetStatus(Task.Status, Task.Progress);

        var group = Task.GroupId.HasValue
            ? await _dbcontext.TaskGroups.FirstOrDefaultAsync(item => item.Id == Task.GroupId.Value && item.ProjectId == ProjectId && !item.IsDeleted)
            : null;
        if (Task.GroupId.HasValue && group == null)
        {
            ModelState.AddModelError("Task.GroupId", "任务分组不存在");
            await LoadSelectListsAsync(ProjectId, Task.Id);
            return Page();
        }

        if (Task.Id == 0)
        {
            Task.ProjectId = group?.ProjectId ?? ProjectId;
            Task.CreatorId = currentUserId.Value;
            Task.CreatedAt = AppTime.Now;
            Task.UpdatedAt = AppTime.Now;
            if (Task.AssigneeType == TaskAssigneeType.DigitalEmployee)
            {
                Task.AgentAssignmentVersion = 1;
                Task.AgentExecutionStatus = AgentTaskExecutionStatus.AwaitingDispatchConfirmation;
            }
            else
            {
                Task.AgentAssignmentVersion = 0;
                Task.AgentExecutionStatus = AgentTaskExecutionStatus.None;
            }
            _dbcontext.ToDoTasks.Add(Task);
            await _dbcontext.SaveChangesAsync();
            await SyncLabelsAsync(Task, LabelNames);
            if (Task.AssigneeType == TaskAssigneeType.DigitalEmployee)
            {
                var dispatch = await _agentDispatch.DispatchTaskAsync(Task.Id, currentUserId.Value);
                TempData["TaskMessage"] = dispatch.AssignedAutomatically
                    ? $"调度 Agent 已自动分配给「{dispatch.Candidates[0].AgentName}」"
                    : "请到任务详情查看匹配结果，选择执行者或调整任务";
                if (!dispatch.AssignedAutomatically) dispatchConfirmationTaskId = Task.Id;
            }
            if (Task.Status == ToDoTaskStatus.PendingConfirmation)
                await NotifyReviewersAsync(Task);
            await _taskDomainService.LogTaskOperationAsync(OperationType.创建, OperationTarget.任务, currentUserId.Value,
                projectId: Task.ProjectId, taskId: Task.Id, taskTitle: Task.Title, targetId: Task.Id,
                afterState: $"创建任务：{Task.Title}", status: OperationStatus.成功);
            await _eventBus.PublishAsync(
                "task.created",
                new { taskId = Task.Id, projectId = Task.ProjectId, Task.Title, Task.Status, operatedByUserId = currentUserId.Value },
                "Task",
                Task.Id.ToString());
        }
        else
        {
            var entity = await _dbcontext.ToDoTasks.FirstOrDefaultAsync(item => item.Id == Task.Id);
            if (entity == null) return NotFound();
            ProjectId = entity.ProjectId;
            var original = new TodoTask
            {
                Title = entity.Title,
                Description = entity.Description,
                AssigneeId = entity.AssigneeId,
                ClaimerId = entity.ClaimerId,
                Status = entity.Status,
                Progress = entity.Progress,
                Priority = entity.Priority,
                EndTime = entity.EndTime,
                CreatorId = entity.CreatorId,
                ParentTaskId = entity.ParentTaskId,
                ReviewerId = entity.ReviewerId,
                AssigneeType = entity.AssigneeType,
                AgentName = entity.AgentName,
                AgentDefinitionId = entity.AgentDefinitionId
            };
            var originalStatus = entity.Status;
            if (entity.AssigneeType == TaskAssigneeType.DigitalEmployee
                && Task.AssigneeType == TaskAssigneeType.DigitalEmployee
                && entity.AgentDefinitionId.HasValue)
            {
                // 普通编辑不触发重新派单，保留已经确认的 Agent。
                Task.AgentDefinitionId = entity.AgentDefinitionId;
                Task.AgentName = entity.AgentName;
            }
            var awaitingRedispatch = entity.AssigneeType == TaskAssigneeType.DigitalEmployee
                && Task.AssigneeType == TaskAssigneeType.DigitalEmployee
                && !entity.AgentDefinitionId.HasValue;
            var assignmentChanged = entity.AssigneeType != Task.AssigneeType
                || entity.AgentDefinitionId != Task.AgentDefinitionId
                || awaitingRedispatch;
            entity.Title = Task.Title;
            entity.Description = Task.Description;
            entity.AssigneeId = Task.AssigneeId;
            entity.ClaimerId = Task.ClaimerId;
            entity.SetStatus(Task.Status, Task.Progress);
            entity.Priority = Task.Priority;
            entity.EndTime = Task.EndTime;
            entity.ParentTaskId = Task.ParentTaskId;
            entity.ReviewerId = Task.ReviewerId;
            entity.AssigneeType = Task.AssigneeType;
            entity.AgentName = Task.AgentName;
            entity.AgentDefinitionId = Task.AgentDefinitionId;
            if (assignmentChanged)
            {
                entity.AgentAssignmentVersion++;
                entity.AgentExecutionStatus = entity.AssigneeType == TaskAssigneeType.DigitalEmployee
                    ? entity.AgentDefinitionId.HasValue
                        ? AgentTaskExecutionStatus.Pending
                        : AgentTaskExecutionStatus.AwaitingDispatchConfirmation
                    : AgentTaskExecutionStatus.None;
                entity.AgentLastError = string.Empty;
            }
            else if (entity.AssigneeType == TaskAssigneeType.DigitalEmployee
                     && originalStatus is ToDoTaskStatus.Completed or ToDoTaskStatus.Cancelled
                     && entity.Status is not (ToDoTaskStatus.Completed or ToDoTaskStatus.Cancelled))
            {
                entity.AgentAssignmentVersion++;
                entity.AgentExecutionStatus = AgentTaskExecutionStatus.Pending;
                entity.AgentLastError = string.Empty;
                assignmentChanged = true;
            }
            if (entity.Status == ToDoTaskStatus.Completed)
                entity.AgentExecutionStatus = entity.AssigneeType == TaskAssigneeType.DigitalEmployee
                    ? AgentTaskExecutionStatus.Completed
                    : AgentTaskExecutionStatus.None;
            else if (entity.Status == ToDoTaskStatus.Cancelled)
                entity.AgentExecutionStatus = AgentTaskExecutionStatus.None;
            entity.UpdatedAt = AppTime.Now;
            if (entity.Status == ToDoTaskStatus.InProgress && entity.StartTime == null) entity.StartTime = AppTime.Now;
            try
            {
                await _dbcontext.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                ModelState.AddModelError(string.Empty, "保存时任务已发生变化，请刷新页面后重试");
                await LoadSelectListsAsync(ProjectId, Task.Id);
                return Page();
            }

            var nextIdempotencyKey = entity.AssigneeType == TaskAssigneeType.DigitalEmployee
                && entity.AgentDefinitionId.HasValue
                && entity.Status is not (ToDoTaskStatus.Completed or ToDoTaskStatus.Cancelled)
                ? BuildAssignmentIdempotencyKey(entity)
                : null;
            if (assignmentChanged || entity.Status is ToDoTaskStatus.Completed or ToDoTaskStatus.Cancelled)
                await _agentWorkQueue.CancelPendingForTaskAsync(entity.Id, nextIdempotencyKey);
            if (assignmentChanged && nextIdempotencyKey != null)
            {
                await _agentWorkQueue.EnqueueTaskAsync(
                    entity.Id,
                    currentUserId.Value,
                    AgentWorkTriggerType.TaskAssigned,
                    entity.Id,
                    AgentWorkQueueService.BuildAssignmentPrompt(entity),
                    nextIdempotencyKey);
            }
            else if (assignmentChanged
                     && entity.AssigneeType == TaskAssigneeType.DigitalEmployee
                     && !entity.AgentDefinitionId.HasValue)
            {
                var dispatch = await _agentDispatch.DispatchTaskAsync(entity.Id, currentUserId.Value);
                TempData["TaskMessage"] = dispatch.AssignedAutomatically
                    ? $"调度 Agent 已自动分配给「{dispatch.Candidates[0].AgentName}」"
                    : "请到任务详情查看匹配结果，选择执行者或调整任务";
                if (!dispatch.AssignedAutomatically) dispatchConfirmationTaskId = entity.Id;
            }
            await SyncLabelsAsync(entity, LabelNames);
            if (entity.Status == ToDoTaskStatus.PendingConfirmation)
                await NotifyReviewersAsync(entity);

            var changed = await _taskDomainService.GenerateChangeSummaryAsync(original, entity);
            if (changed.Count > 0)
            {
                await _taskDomainService.LogTaskOperationAsync(OperationType.更新, OperationTarget.任务, currentUserId.Value,
                    projectId: entity.ProjectId, taskId: entity.Id, taskTitle: entity.Title, targetId: entity.Id,
                    beforeState: string.Join("\n", changed.Select(item => $"{item.FieldName}: {item.BeforeValue}")),
                    afterState: string.Join("\n", changed.Select(item => $"{item.FieldName}: {item.AfterValue}")),
                    status: OperationStatus.成功);
                await _eventBus.PublishAsync(
                    "task.updated",
                    new
                    {
                        taskId = entity.Id,
                        projectId = entity.ProjectId,
                        entity.Title,
                        entity.Status,
                        operatedByUserId = currentUserId.Value,
                        changes = changed.Select(item => new { item.FieldName, item.BeforeValue, item.AfterValue })
                    },
                    "Task",
                    entity.Id.ToString());
            }
        }

        if (dispatchConfirmationTaskId.HasValue)
            return RedirectToPage("/Tasks/Details", new { id = dispatchConfirmationTaskId.Value });
        if (!string.IsNullOrWhiteSpace(ReturnPage) && ReturnId.HasValue)
            return RedirectToPage("/" + ReturnPage, new { id = ReturnId.Value });
        return RedirectToPage("/Tasks/Index", new { projectId = Task.ProjectId });
    }

    private async Task LoadSelectListsAsync(int projectId, int? currentTaskId)
    {
        GroupList = new SelectList(await _dbcontext.TaskGroups
            .Where(item => item.ProjectId == projectId && !item.IsDeleted)
            .OrderBy(item => item.Name).ToListAsync(), "Id", "Name");
        var userIds = await _dbcontext.ProjectUsers
            .Where(item => item.ProjectId == projectId)
            .Select(item => item.UserId)
            .ToListAsync();
        var leaderId = await _dbcontext.Project.AsNoTracking()
            .Where(item => item.Id == projectId && !item.IsDeleted)
            .Select(item => item.LeaderUserId)
            .FirstOrDefaultAsync();
        if (leaderId > 0) userIds.Add(leaderId);
        var users = await _dbcontext.Users.AsNoTracking()
            .Where(item => userIds.Distinct().Contains(item.Id) && !item.IsDeleted && item.Status == UserStatus.Active)
            .OrderBy(item => item.UserName)
            .ToListAsync();
        var choices = users.Select(item => new { item.Id, Name = string.IsNullOrWhiteSpace(item.RealName)
            ? item.UserName : $"{item.RealName} ({item.UserName})" }).ToList();
        UserList = new SelectList(choices, "Id", "Name");
        ReviewerList = new SelectList(choices, "Id", "Name");
        ParentTaskList = new SelectList(await _dbcontext.ToDoTasks
            .Where(item => item.ProjectId == projectId && !item.IsDeleted && item.Id != currentTaskId)
            .OrderBy(item => item.Title).ToListAsync(), "Id", "Title");
    }

    private static string BuildAssignmentIdempotencyKey(TodoTask task)
        => $"task-assigned:{task.Id}:{task.AgentDefinitionId}:{task.AgentAssignmentVersion}";

    private async Task<bool> CanEditTaskAsync(int projectId, int userId, int? taskId)
    {
        if (!await _dbcontext.Project.AnyAsync(p => p.Id == projectId && !p.IsDeleted && p.Status == ProjectStatus.Active)) return false;
        var role = await _dbcontext.Users.AsNoTracking()
            .Where(item => item.Id == userId)
            .Select(item => (UserRole?)item.Role)
            .FirstOrDefaultAsync();
        if (role == UserRole.systemAdmin) return true;
        if (await _dbcontext.Project.AsNoTracking()
            .AnyAsync(item => item.Id == projectId && !item.IsDeleted && item.LeaderUserId == userId)) return true;
        if (await _dbcontext.ProjectUsers.AsNoTracking()
            .AnyAsync(item => item.ProjectId == projectId && item.UserId == userId && item.ProjectRole == (int)ProjectRole.Admin)) return true;
        if (!taskId.HasValue)
            return await _dbcontext.ProjectUsers.AsNoTracking()
                .AnyAsync(item => item.ProjectId == projectId && item.UserId == userId);
        return await _dbcontext.ToDoTasks.AsNoTracking()
            .AnyAsync(item => item.Id == taskId.Value && item.ProjectId == projectId && item.CreatorId == userId && !item.IsDeleted);
    }

    private async Task SyncLabelsAsync(TodoTask task, string? labelNames)
    {
        var names = (labelNames ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length <= 50)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var oldLinks = await _dbcontext.TaskLabelLinks.Where(item => item.TaskId == task.Id).ToListAsync();
        _dbcontext.TaskLabelLinks.RemoveRange(oldLinks);
        foreach (var name in names)
        {
            var label = await _dbcontext.TaskLabels.FirstOrDefaultAsync(item => item.ProjectId == task.ProjectId && !item.IsDeleted && item.Name == name);
            if (label == null)
            {
                label = new TaskLabel { ProjectId = task.ProjectId, Name = name };
                _dbcontext.TaskLabels.Add(label);
                await _dbcontext.SaveChangesAsync();
            }
            _dbcontext.TaskLabelLinks.Add(new TaskLabelLink { TaskId = task.Id, LabelId = label.Id });
        }
        await _dbcontext.SaveChangesAsync();
    }

    private async Task NotifyReviewersAsync(TodoTask task)
    {
        var reviewerIds = await new AttentionRecipientService(_dbcontext).TaskAsync(task);
        await _notifications.NotifyManyAsync(reviewerIds, "任务待审核", $"任务「{task.Title}」已完成执行，请进行人工审核。", "Review", $"/Tasks/Details/{task.Id}");
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.TaskGroups
{
    public class EditModel : PageModel
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly TaskGroupDomainService _taskGroupDomainService;
        private readonly ToDoTaskDomainService _taskDomainService;
        [BindProperty]
        public List<SelectListItem> ProjectList { get; set; } = new();
        [BindProperty(SupportsGet = true)]
        public int? Id { get; set; }
        [BindProperty(SupportsGet = true)]
        public int ProjectId { get; set; }
        public int CurrentProjectId { get; set; }
        [BindProperty]
        public int OriginalProjectId { get; set; }
        public EditModel(ApplicationDbContext context, TaskGroupDomainService taskGroupDomainService, ToDoTaskDomainService taskDomainService)
        {
            _dbContext = context;
            _taskGroupDomainService = taskGroupDomainService;
            _taskDomainService = taskDomainService;
        }
        public int? CurrentUserId
        {
            get
            {
                var idClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
                if (idClaim != null && int.TryParse(idClaim.Value, out int userId))
                {
                    return userId;
                }
                return null;
            }
        }

        [BindProperty]
        public TaskGroup TaskGroup { get; set; } = null!;

        public async Task<IActionResult> OnGetAsync(int? id, int? projectId)
        {
            if (!CurrentUserId.HasValue) return Challenge();

            if (!id.HasValue || id.Value == 0)
            {
                if (!projectId.HasValue) return BadRequest("缺少项目ID");
                if (!await CanManageProjectAsync(projectId.Value, CurrentUserId.Value)) return Forbid();

                CurrentProjectId = projectId.Value;
                await LoadProjectListAsync(CurrentProjectId);
                TaskGroup = new TaskGroup
                {
                    CreatorId = CurrentUserId.Value,
                    ProjectId = CurrentProjectId
                };
                return Page();
            }

            var existingGroup = await _dbContext.TaskGroups.AsNoTracking()
                .FirstOrDefaultAsync(group => group.Id == id.Value);
            if (existingGroup == null) return NotFound();
            TaskGroup = existingGroup;
            if (!await CanManageProjectAsync(TaskGroup.ProjectId, CurrentUserId.Value)) return Forbid();

            CurrentProjectId = TaskGroup.ProjectId;
            OriginalProjectId = TaskGroup.ProjectId;
            await LoadProjectListAsync(CurrentProjectId);

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!CurrentUserId.HasValue) return Challenge();

            if (TaskGroup.Id == 0)
            {
                var targetProjectId = TaskGroup.ProjectId;
                if (!await CanManageProjectAsync(targetProjectId, CurrentUserId.Value)) return Forbid();
                CurrentProjectId = targetProjectId;
                await LoadProjectListAsync(CurrentProjectId);
                if (!ModelState.IsValid) return Page();

                var group = new TaskGroup
                {
                    Name = TaskGroup.Name?.Trim() ?? string.Empty,
                    Description = TaskGroup.Description?.Trim(),
                    ProjectId = targetProjectId,
                    CreatorId = CurrentUserId.Value,
                    CreatedAt = AppTime.Now,
                    UpdatedAt = AppTime.Now
                };

                _dbContext.TaskGroups.Add(group);
                await _dbContext.SaveChangesAsync();

                var afterState =
                    $"ID: {group.Id}\n" +
                    $"名称: {group.Name}\n" +
                    $"描述: {group.Description}\n" +
                    $"所属项目ID: {group.ProjectId}\n" +
                    $"创建时间: {group.CreatedAt:yyyy-MM-dd HH:mm}\n" +
                    $"创建人ID: {group.CreatorId}\n";

                // 插入日志
                await _taskGroupDomainService.LogTaskGroupOperationAsync(
                    OperationType.创建,             // 新建操作
                    OperationTarget.分组,        // 操作对象
                    CurrentUserId.Value,           // 操作人
                    afterState: afterState,
                    projectId: group.ProjectId,
                    targetId: group.Id,
                    targetName: group.Name
                );

                TempData["Message"] = "保存成功！";
                return RedirectToPage("/TaskGroups/Index", new { projectId = group.ProjectId });
            }

            var existingGroup = await _dbContext.TaskGroups
                .FirstOrDefaultAsync(group => group.Id == TaskGroup.Id);
            if (existingGroup == null) return NotFound();
            if (!await CanManageProjectAsync(existingGroup.ProjectId, CurrentUserId.Value)) return Forbid();

            CurrentProjectId = existingGroup.ProjectId;
            OriginalProjectId = existingGroup.ProjectId;
            TaskGroup.ProjectId = existingGroup.ProjectId;
            await LoadProjectListAsync(CurrentProjectId);
            if (!ModelState.IsValid) return Page();

            var original = new TaskGroup
            {
                Name = existingGroup.Name,
                Description = existingGroup.Description,
                ProjectId = existingGroup.ProjectId,
                CreatorId = existingGroup.CreatorId,
                CreatedAt = existingGroup.CreatedAt,
                UpdatedAt = existingGroup.UpdatedAt
            };
            existingGroup.Name = TaskGroup.Name?.Trim() ?? string.Empty;
            existingGroup.Description = TaskGroup.Description?.Trim();
            existingGroup.UpdatedAt = AppTime.Now;

            var changes = await _taskGroupDomainService.GenerateGroupChangeSummaryAsync(original, existingGroup);
            await _dbContext.SaveChangesAsync();

            await _taskGroupDomainService.LogTaskGroupOperationAsync(
                OperationType.更新,
                OperationTarget.分组,
                CurrentUserId.Value,
                beforeState: changes.Any() ? string.Join("\n", changes.Select(change => $"{change.FieldName}：{change.BeforeValue}")) : null,
                afterState: changes.Any() ? string.Join("\n", changes.Select(change => $"{change.FieldName}：{change.AfterValue}")) : null,
                projectId: existingGroup.ProjectId,
                targetId: existingGroup.Id,
                targetName: existingGroup.Name);

            TempData["Message"] = "保存成功！";
            return RedirectToPage("/TaskGroups/Index", new { projectId = existingGroup.ProjectId });
        }
        public async Task<IActionResult> OnPostDeleteAsync()
        {
            if (TaskGroup.Id == 0 || CurrentUserId == null)
            {
                return BadRequest();
            }

            var groupToDelete = await _dbContext.TaskGroups
                .FirstOrDefaultAsync(g => g.Id == TaskGroup.Id);

            if (groupToDelete == null)
            {
                return NotFound();
            }

            if (!await CanManageProjectAsync(groupToDelete.ProjectId, CurrentUserId.Value))
            {
                return Forbid();
            }

            //_dbContext.TaskGroups.Remove(groupToDelete);
            var projectId = groupToDelete.ProjectId; // 先保存一份
            groupToDelete.IsDeleted = true; // 逻辑删除

            // 删除该分组下的所有任务（逻辑删除）
            var tasks = await _dbContext.ToDoTasks
                .Where(t => t.GroupId == groupToDelete.Id)
                .ToListAsync();

            foreach (var task in tasks)
            {
                task.IsDeleted = true;
                var beforeState =
                    $"标题: {task.Title}\n" +
                    $"描述: {task.Description}\n" +
                    $"状态: {task.Status}\n" +
                    $"负责人ID: {task.AssigneeId}\n" +
                    $"截止时间: {task.EndTime:yyyy-MM-dd HH:mm}\n" +
                    $"优先级: {task.Priority}\n";
                // 生成删除日志
                await _taskDomainService.LogTaskOperationAsync(
                    operationType: OperationType.删除,
                    target: OperationTarget.任务,
                    operatorUserId: CurrentUserId.Value,
                    projectId: task.ProjectId,
                    taskId: task.Id,
                    taskTitle: task.Title,
                    taskName: task.Title,
                    targetId: task.Id,
                    beforeState: beforeState,
                    afterState: "已删除",
                    status: OperationStatus.成功
                );
            }

            await _dbContext.SaveChangesAsync();

            await _taskGroupDomainService.LogTaskGroupOperationAsync(
                OperationType.删除,
                OperationTarget.分组,
                CurrentUserId.Value,
                beforeState: $"分组名称：{groupToDelete.Name}\n描述：{groupToDelete.Description}",
                afterState: null,
                projectId: groupToDelete.ProjectId,
                targetId: groupToDelete.Id,
                targetName: groupToDelete.Name
            );

            TempData["Message"] = "分组已成功删除！";
            return RedirectToPage("/TaskGroups/Index", new { projectId = projectId });
        }

        private Task LoadProjectListAsync(int projectId) => LoadProjectListCoreAsync(projectId);

        private async Task LoadProjectListCoreAsync(int projectId)
        {
            ProjectList = await _dbContext.Project.AsNoTracking()
                .Where(project => !project.IsDeleted && project.Id == projectId)
                .Select(project => new SelectListItem(project.Name, project.Id.ToString()))
                .ToListAsync();
        }

        private async Task<bool> CanManageProjectAsync(int projectId, int userId)
        {
            if (User.IsInRole(nameof(UserRole.systemAdmin))) return true;
            if (await _dbContext.Project.AsNoTracking().AnyAsync(project =>
                project.Id == projectId && !project.IsDeleted && project.LeaderUserId == userId)) return true;
            return await _dbContext.ProjectUsers.AsNoTracking().AnyAsync(member =>
                member.ProjectId == projectId
                && member.UserId == userId
                && member.ProjectRole == (int)ProjectRole.Admin);
        }
    }
}

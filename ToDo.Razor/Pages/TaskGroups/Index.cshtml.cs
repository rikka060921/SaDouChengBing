using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;
using SystemTask = System.Threading.Tasks.Task;

namespace ToDo.Razor.Pages.TaskGroups
{
    public class IndexModel : PageModel
    {
        private readonly ApplicationDbContext _dbcontext;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IAntiforgery _antiforgery;
        private readonly TaskGroupDomainService _taskGroupDomainService;
        private readonly ToDoTaskDomainService _taskDomainService;

        public bool ProjectRole { get; set; } // 判断是否是项目管理员

        public List<TaskGroup> TaskGroups { get; set; } = new();
        [BindProperty(SupportsGet = true)]
        public int CurrentProjectId { get; set; } // 当前项目ID
        [BindProperty(SupportsGet = true)]
        public int Id { get; set; } // 任务组id
        [BindProperty(SupportsGet = true)]
        public bool ShowDeleted { get; set; } = false; // 是否显示已删除的任务组，false不显示
        public string RequestVerificationToken { get; private set; } = string.Empty;

        public int? TaskGroupCurrentUserId
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


        public IndexModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IAntiforgery antiforgery, TaskGroupDomainService taskGroupDomainService, ToDoTaskDomainService taskDomainService)
        {
            _dbcontext = context;
            _userManager = userManager;
            _antiforgery = antiforgery;
            _taskGroupDomainService = taskGroupDomainService;
            _taskDomainService = taskDomainService;
        }

        public async Task<IActionResult> OnGetAsync(int? projectId, int? id)
        {
            if (projectId == null)
            {
                // 处理异常或跳转
                return RedirectToPage("/Error");
            }
            CurrentProjectId = projectId.Value;
            if (!TaskGroupCurrentUserId.HasValue) return Challenge();
            if (!await CanAccessProjectAsync(CurrentProjectId, TaskGroupCurrentUserId.Value)) return Forbid();
            var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
            RequestVerificationToken = tokens.RequestToken ?? string.Empty;
            //RequestVerificationToken = _antiforgery.GetAndStoreTokens(HttpContext).RequestToken;

            // 获取当前用户ID
            //var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            //var userIdStr = HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
            var userIdStr = User?.FindFirstValue(ClaimTypes.NameIdentifier)
    ?? HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
            //var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            ProjectRole = !string.IsNullOrEmpty(userIdStr)
                && int.TryParse(userIdStr, out int currentUserId)
                && await CanManageProjectAsync(CurrentProjectId, currentUserId);

            IQueryable<TaskGroup> query = _dbcontext.TaskGroups
                .Where(g => g.ProjectId == CurrentProjectId)
                .Include(g => g.Project);
            if (!ProjectRole || !ShowDeleted)
            {
                query = query.Where(g => !g.IsDeleted);
            }
            TaskGroups = await query.ToListAsync();

            return Page();
        }

        public async Task<IActionResult> OnPostDeleteGroupAsync(int projectid, int id)
        {
            if (TaskGroupCurrentUserId == null)
                return Unauthorized();

            var group = await _dbcontext.TaskGroups.FindAsync(id);
            if (group == null)
                return NotFound();

            if (!await CanManageProjectAsync(group.ProjectId, TaskGroupCurrentUserId.Value))
                return Forbid();

            var projectId = group.ProjectId;
            group.IsDeleted = true;

            // 删除该分组下的所有任务（逻辑删除）
            var tasks = await _dbcontext.ToDoTasks
                .Where(t => t.GroupId == group.Id)
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
                    operatorUserId: TaskGroupCurrentUserId.Value,
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

            await _dbcontext.SaveChangesAsync();

            await _taskGroupDomainService.LogTaskGroupOperationAsync(
                OperationType.删除,
                OperationTarget.分组,
                TaskGroupCurrentUserId.Value,
                beforeState: $"分组名称：{group.Name}\n描述：{group.Description}",
                afterState: null,
                projectId: group.ProjectId,
                targetId: group.Id,
                targetName: group.Name
            );

            TempData["Message"] = "任务组已删除";

            return RedirectToPage(new { projectId = projectId });
        }
        public async Task<IActionResult> OnPostRestoreAsync(int id)
        {
            var taskGroup = await _dbcontext.TaskGroups.FindAsync(id);
            if (taskGroup == null)
                return new JsonResult(new { success = false, message = "任务组不存在" });

            if (!taskGroup.IsDeleted)
                return new JsonResult(new { success = false, message = "任务组未被删除" });

            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out var userId))
                return new JsonResult(new { success = false, message = "用户未登录" });

            // 权限判断：是否是管理员
            var isAdmin = await CanManageProjectAsync(taskGroup.ProjectId, userId);
            if (!isAdmin)
                return new JsonResult(new { success = false, message = "无权限" });

            taskGroup.IsDeleted = false;
            

            // 记录日志
            await _taskGroupDomainService.LogTaskGroupOperationAsync(
                OperationType.恢复,
                OperationTarget.分组,
                userId,
                beforeState: "已删除",
                afterState: "已恢复",
                projectId: taskGroup.ProjectId,
                targetId: taskGroup.Id,
                targetName: taskGroup.Name
            );

            await _dbcontext.SaveChangesAsync();

            return new JsonResult(new { success = true, message = "恢复成功" });
        }

        private async Task<bool> CanAccessProjectAsync(int projectId, int userId)
        {
            if (!await _dbcontext.Project.AnyAsync(p => p.Id == projectId && !p.IsDeleted && p.Status == ProjectStatus.Active)) return false;
            if (User.IsInRole(nameof(UserRole.systemAdmin))) return true;
            if (await _dbcontext.Project.AsNoTracking().AnyAsync(project =>
                project.Id == projectId && !project.IsDeleted && project.LeaderUserId == userId)) return true;
            return await _dbcontext.ProjectUsers.AsNoTracking().AnyAsync(member =>
                member.ProjectId == projectId && member.UserId == userId);
        }

        private async Task<bool> CanManageProjectAsync(int projectId, int userId)
        {
            if (User.IsInRole(nameof(UserRole.systemAdmin))) return true;
            if (await _dbcontext.Project.AsNoTracking().AnyAsync(project =>
                project.Id == projectId && !project.IsDeleted && project.LeaderUserId == userId)) return true;
            return await _dbcontext.ProjectUsers.AsNoTracking().AnyAsync(member =>
                member.ProjectId == projectId
                && member.UserId == userId
                && member.ProjectRole == (int)global::ProjectRole.Admin);
        }

    }
}

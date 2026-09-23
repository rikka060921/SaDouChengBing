using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ToDo.Context;
using ToDo.Entities;
using ToDo.Entities.Dto;
using ToDo.Domain;
using Microsoft.Extensions.Logging;

namespace ToDo.Razor.Pages.Projects
{
    public class DetailsModel : PageModel
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<DetailsModel> _logger;
        private readonly ProjectDomain _projectDomain;
        public int CurrentProjectId { get; set; }

        public DetailsModel(
            ApplicationDbContext context,
            ILogger<DetailsModel> logger,
            ProjectDomain projectDomain)
        {
            _context = context;
            _logger = logger;
            _projectDomain = projectDomain;
        }

        public ProjectListDto Project { get; set; } = new();
        public int TaskCount { get; set; }
        public List<ProjectMemberDto> Members { get; set; } = new();
        public bool CanManageMembers { get; set; }

        [BindProperty]
        public AddMemberInput Input { get; set; } = new AddMemberInput();

        public List<SelectListItem> ActiveUsers { get; set; } = new();

        // 添加属性来获取当前用户信息
        public int CurrentUserId => int.TryParse(
            User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : 0;
        public string CurrentUserRoleString => User.FindFirstValue(ClaimTypes.Role) ?? nameof(UserRole.teamMember);
        public UserRole CurrentUserRole => Enum.TryParse<UserRole>(CurrentUserRoleString, out var role)
            ? role
            : UserRole.teamMember;

        // 当前用户是否是项目管理员（ProjectRole.Admin）
        public bool IsCurrentUserAdmin => Members.Any(m => m.UserId == CurrentUserId && m.IsAdmin);

        // 当前用户是否是系统管理员
        public bool IsCurrentUserSystemAdmin => CurrentUserRole == UserRole.systemAdmin;

        public class AddMemberInput
        {
            [Required(ErrorMessage = "请选择用户")]
            public int UserId { get; set; }

            public bool MakeAdmin { get; set; }
        }

        public async Task<IActionResult> OnGetAsync(int? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            CurrentProjectId = id.Value;

            if (CurrentUserId <= 0) return Challenge();
            if (!await _projectDomain.CanViewProjectAsync(id.Value, CurrentUserId, CurrentUserRole))
                return Forbid();

            try
            {
                await LoadPageData(id.Value);
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载项目详情时出错");
                ModelState.AddModelError("", "加载项目详情时出错");
                return Page();
            }
        }

        // 新增：获取可添加用户列表的 Handler
        public async Task<JsonResult> OnGetActiveUsersAsync(int id)
        {
            try
            {
                if (CurrentUserId <= 0 || !await HasManageMembersPermissionAsync(id))
                {
                    return new JsonResult(new { success = false, message = "无权限查看可添加用户" })
                    {
                        StatusCode = StatusCodes.Status403Forbidden
                    };
                }

                // 获取当前项目成员ID列表
                var memberUserIds = await _context.ProjectUsers
                    .Where(pu => pu.ProjectId == id)
                    .Select(pu => pu.UserId)
                    .ToListAsync();

                // 获取所有活跃用户（排除已是项目成员的用户）
                var activeUsers = await _context.Users
                    .Where(u => u.Status == UserStatus.Active && !memberUserIds.Contains(u.Id))
                    .OrderBy(u => u.RealName)
                    .Select(u => new
                    {
                        Value = u.Id.ToString(),
                        Text = u.RealName != null ? $"{u.RealName} ({u.UserName})" : u.UserName
                    })
                    .ToListAsync();

                return new JsonResult(new
                {
                    success = true,
                    users = activeUsers
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取可添加用户列表时出错");
                return new JsonResult(new
                {
                    success = false,
                    message = "获取用户列表失败"
                });
            }
        }

        public async Task<IActionResult> OnPostAddMemberAsync(int id)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    await LoadPageData(id);
                    return Page();
                }

                if (Input.UserId <= 0)
                {
                    ModelState.AddModelError("", "请选择有效的用户");
                    await LoadPageData(id);
                    return Page();
                }

                // 修复权限检查：使用统一的权限检查方法
                if (!await HasManageMembersPermissionAsync(id))
                {
                    ModelState.AddModelError("", "无权限添加成员");
                    await LoadPageData(id);
                    return Page();
                }

                // 使用 ProjectDomain 添加成员
                var success = await _projectDomain.AddProjectMemberAsync(
                    projectId: id,
                    userId: Input.UserId,
                    currentUserId: CurrentUserId,
                    currentUserRole: CurrentUserRole,
                    makeAdmin: Input.MakeAdmin);

                if (success)
                {
                    TempData["SuccessMessage"] = "成员添加成功";
                    return RedirectToPage(new { id });
                }

                ModelState.AddModelError("", "添加成员失败，请检查用户是否已在项目中");
                await LoadPageData(id);
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加成员时出错，项目ID: {ProjectId}", id);
                ModelState.AddModelError("", "添加成员失败，请稍后重试");
                await LoadPageData(id);
                return Page();
            }
        }

        [BindProperty]
        public int TargetUserId { get; set; }

        public async Task<JsonResult> OnPostToggleAdminAsync(int id)
        {
            try
            {
                // 修复权限检查：使用统一的权限检查方法
                if (!await HasManageMembersPermissionAsync(id))
                {
                    return new JsonResult(new { success = false, message = "无权限操作" });
                }

                var success = await _projectDomain.ToggleAdminStatusAsync(
                    projectId: id,
                    targetUserId: TargetUserId,
                    currentUserId: CurrentUserId,
                    currentUserRole: CurrentUserRole);

                if (!success)
                {
                    var isLastAdmin = await _projectDomain.IsLastAdminAsync(id, TargetUserId);
                    var message = isLastAdmin
                        ? "操作失败：项目必须至少保留一位管理员"
                        : "更新管理员状态失败，请检查权限";

                    return new JsonResult(new { success = false, message });
                }

                return new JsonResult(new { success = true, message = "管理员状态更新成功" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "切换管理员状态时出错");
                return new JsonResult(new
                {
                    success = false,
                    message = "操作失败，请稍后重试"
                });
            }
        }

        public async Task<JsonResult> OnPostRemoveMemberAsync(int id)
        {
            try
            {
                // 如果是移除自己，不需要管理员权限
                if (TargetUserId != CurrentUserId)
                {
                    // 如果是移除他人，需要项目管理员或系统管理员权限
                    if (!await HasManageMembersPermissionAsync(id))
                    {
                        return new JsonResult(new { success = false, message = "无权限移除成员" });
                    }
                }

                var success = await _projectDomain.RemoveProjectMemberAsync(
                    projectId: id,
                    targetUserId: TargetUserId,
                    currentUserId: CurrentUserId,
                    currentUserRole: CurrentUserRole);

                if (!success)
                {
                    var isLastAdmin = await _projectDomain.IsLastAdminAsync(id, TargetUserId);
                    var message = isLastAdmin
                        ? "操作失败：不能移除最后一位管理员"
                        : "移除成员失败，请检查权限";

                    return new JsonResult(new { success = false, message });
                }

                return new JsonResult(new { success = true, message = "成员移除成功" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "移除成员时出错");
                return new JsonResult(new
                {
                    success = false,
                    message = "操作失败，请稍后重试"
                });
            }
        }

        // 新增：统一的权限检查方法
        private async Task<bool> HasManageMembersPermissionAsync(int projectId)
        {
            // 系统管理员直接通过
            if (IsCurrentUserSystemAdmin)
                return true;

            // 项目管理员检查：使用 ProjectDomain 进行准确的权限验证
            return await _projectDomain.IsProjectAdminAsync(projectId, CurrentUserId);
        }

        // 辅助方法：检查是否可以移除成员
        public bool CanRemoveMember(int targetUserId)
        {
            // 如果是移除自己
            if (targetUserId == CurrentUserId)
            {
                // 普通成员可以随时退出
                var isTargetAdmin = Members.FirstOrDefault(m => m.UserId == targetUserId)?.IsAdmin ?? false;
                if (!isTargetAdmin)
                {
                    return true; // 普通成员随时可以退出
                }

                // 管理员只能在管理员数量>1时退出
                var adminCount = GetAdminCount();
                return adminCount > 1;
            }

            // 如果是移除他人，需要项目管理员或系统管理员权限
            return IsCurrentUserAdmin || IsCurrentUserSystemAdmin;
        }

        // 辅助方法：检查是否可以切换管理员状态
        public bool CanToggleAdmin(int targetUserId, bool isCurrentlyAdmin)
        {
            // 修复权限检查：项目管理员和系统管理员都可以切换管理员状态
            if (!IsCurrentUserAdmin && !IsCurrentUserSystemAdmin)
                return false;

            // 获取管理员数量
            var adminCount = GetAdminCount();

            // 如果是最后一个管理员且试图取消管理员权限，则不允许
            if (isCurrentlyAdmin && adminCount <= 1)
                return false;

            return true;
        }

        // 辅助方法：获取管理员数量
        public int GetAdminCount() => Members.Count(m => m.IsAdmin);

        private async Task LoadPageData(int id)
        {
            // 查询项目基本信息
            var project = await _context.Project
                .Include(p => p.CreatedByUser)
                .Include(p => p.LeaderUser)
                .Include(p => p.ProjectUsers)
                .Where(p => p.Id == id && !p.IsDeleted)
                .Select(p => new ProjectListDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    Description = p.Description ?? string.Empty,
                    CreatedAt = p.CreatedAt,
                    CreatedByUserName = p.CreatedByUser.UserName ?? string.Empty,
                    CreatedByUserId = p.CreatedByUserId,
                    LeaderUserId = p.LeaderUserId,
                    LeaderUserName = p.LeaderUser.RealName ?? p.LeaderUser.UserName ?? string.Empty,
                    Requirements = p.Requirements,
                    Status = p.Status,
                    IsCurrentUserLeader = p.LeaderUserId == CurrentUserId,
                    ProjectUsers = p.ProjectUsers.Select(pu => new ProjectUserDto
                    {
                        UserId = pu.UserId,
                        ProjectRole = pu.ProjectRole
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (project == null)
            {
                throw new Exception("项目不存在");
            }

            // 查询任务数
            TaskCount = await _context.ToDoTasks
                .CountAsync(t => t.ProjectId == id && !t.IsDeleted);

            // 查询成员列表（按姓名排序）
            Members = await _context.ProjectUsers
                .Where(pu => pu.ProjectId == id)
                .Include(pu => pu.User)
                .Where(pu => pu.User.Status == UserStatus.Active)
                .OrderBy(pu => pu.User.RealName)
                .Select(pu => new ProjectMemberDto
                {
                    UserId = pu.UserId,
                    UserName = pu.User.UserName ?? string.Empty,
                    RealName = pu.User.RealName ?? pu.User.UserName ?? string.Empty,
                    IsAdmin = pu.ProjectRole == (int)ProjectRole.Admin
                })
                .ToListAsync();

            // 修复权限检查：使用统一的权限检查方法
            CanManageMembers = await HasManageMembersPermissionAsync(id);

            if (CanManageMembers)
            {
                var memberUserIds = Members.Select(member => member.UserId).ToList();
                ActiveUsers = await _context.Users
                    .Where(user => user.Status == UserStatus.Active && !memberUserIds.Contains(user.Id))
                    .OrderBy(user => user.RealName)
                    .Select(user => new SelectListItem
                    {
                        Value = user.Id.ToString(),
                        Text = user.RealName != null ? $"{user.RealName} ({user.UserName})" : user.UserName
                    })
                    .ToListAsync();
            }

            Project = project;
            CurrentProjectId = Project.Id;
        }
    }

    public class ProjectMemberDto
    {
        public int UserId { get; set; }
        public string UserName { get; set; } = "";
        public string RealName { get; set; } = "";
        public bool IsAdmin { get; set; }
    }
}

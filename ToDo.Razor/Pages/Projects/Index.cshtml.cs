using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;
using ToDo.Entities.Dto;

namespace ToDo.Razor.Pages.Projects
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly ProjectDomain _projectDomain;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<IndexModel> _logger;
        private readonly ApplicationDbContext _context;

        public IndexModel(
            ProjectDomain projectDomain,
            UserManager<ApplicationUser> userManager,
            ApplicationDbContext context,
            ILogger<IndexModel> logger)
        {
            _projectDomain = projectDomain;
            _userManager = userManager;
            _logger = logger;
            _context = context;
        }

        [TempData]
        public string? ErrorMessage { get; set; }

        [BindProperty(SupportsGet = true)]
        public int PageIndex { get; set; } = 1;

        [BindProperty(SupportsGet = true)]
        public int PageSize { get; set; } = 10;

        public int TotalCount { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? Keyword { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool? IsArchivedFilter { get; set; } = false;

        [BindProperty(SupportsGet = true)]
        public string View { get; set; } = "active";

        public List<ProjectListDto> Projects { get; set; } = new();

        public async Task OnGetAsync()
        {
            try
            {
                var user = await _userManager.GetUserAsync(User);
                if (user == null) return;

                if (!Request.Query.ContainsKey(nameof(View)) && Request.Query.ContainsKey(nameof(IsArchivedFilter)))
                    View = IsArchivedFilter == true ? "archived" : IsArchivedFilter == false ? "active" : "all";
                View = View is "archived" or "all" ? View : "active";
                IsArchivedFilter = View == "all" ? null : View == "archived";
                PageIndex = Math.Max(1, PageIndex);
                PageSize = Math.Clamp(PageSize, 1, 100);
                var result = await _projectDomain.GetProjectsAsync(
                    currentUserId: user.Id,
                    currentUserRole: GetUserRole(user),
                    pageIndex: PageIndex,
                    pageSize: PageSize,
                    keyword: Keyword,
                    isArchivedFilter: IsArchivedFilter
                );

                TotalCount = result.TotalCount;
                var lastPage = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
                if (PageIndex > lastPage)
                {
                    PageIndex = lastPage;
                    result = await _projectDomain.GetProjectsAsync(user.Id, GetUserRole(user), PageIndex, PageSize, Keyword, IsArchivedFilter);
                }
                Projects = result.Projects;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载项目列表失败");
                Projects = new List<ProjectListDto>();
                TotalCount = 0;
            }
        }

        /// <summary>
        /// 检查当前用户是否有权限管理指定项目
        /// </summary>
        public async Task<bool> CanManageProjectAsync(int projectId)
        {
            try
            {
                var user = await _userManager.GetUserAsync(User);
                if (user == null) return false;

                var userRole = GetUserRole(user);
                return await _projectDomain.CanManageProjectAsync(projectId, user.Id, userRole);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查项目管理权限失败");
                return false;
            }
        }

        public async Task<IActionResult> OnPostArchiveAsync(int projectId, bool shouldArchive, bool confirmUnfinishedTasks = false)
        {
            try
            {
                var user = await _userManager.GetUserAsync(User);
                if (user == null)
                    return JsonResult(false, "用户未登录");

                // 检查权限
                var userRole = GetUserRole(user);
                bool hasPermission = await _projectDomain.CanManageProjectAsync(projectId, user.Id, userRole);

                if (!hasPermission)
                    return JsonResult(false, "无操作权限");

                bool operationSuccess = await _projectDomain.SetArchiveStatusAsync(
                    projectId: projectId,
                    shouldArchive: shouldArchive,
                    currentUserId: user.Id,
                    currentUserRole: userRole,
                    confirmUnfinishedTasks: confirmUnfinishedTasks
                );

                if (!operationSuccess)
                    return JsonResult(false, "操作失败：项目不存在");

                var updatedProject = await _context.Project
                    .FirstOrDefaultAsync(p => p.Id == projectId);
                if (updatedProject == null)
                    return JsonResult(false, "操作失败：项目不存在");

                TempData["SuccessMessage"] = shouldArchive ? "项目已归档，可在“已归档”中查看。" : "项目已恢复；后续自动化从现在开始，不补跑积压工作。";
                return JsonResult(true,
                    shouldArchive ? "项目已归档" : "项目已恢复",
                    new
                    {
                        status = (int)updatedProject.Status,
                        canModify = updatedProject.CanModify
                    });
            }
            catch (InvalidOperationException ex)
            {
                return JsonResult(false, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "归档/恢复操作失败：项目ID {ProjectId}", projectId);
                return JsonResult(false, "操作失败，请稍后重试");
            }
        }

        public async Task<IActionResult> OnGetArchiveCheckAsync(int projectId)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null || !await _projectDomain.CanManageProjectAsync(projectId, user.Id, GetUserRole(user)))
                return JsonResult(false, "无操作权限");
            var check = await ProjectLifecycleRules.CheckArchiveAsync(_context, projectId);
            return JsonResult(check.CanArchive, string.Join("\n", check.Blockers),
                new { unfinishedTasks = check.UnfinishedTasks });
        }

        private UserRole GetUserRole(ApplicationUser user)
        {
            string roleString = user.Role.ToString();
            if (Enum.TryParse<UserRole>(roleString, ignoreCase: true, out var role))
            {
                return role;
            }
            return UserRole.teamMember;
        }

        private JsonResult JsonResult(bool success, string message, object? data = null)
        {
            return new JsonResult(new
            {
                success,
                message,
                data
            });
        }
    }
}

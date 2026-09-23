using Markdig;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.TaskGroups
{
    public class DetailsModel : PageModel
    {
        private const int PageSize = 10; // 每页显示10条数据
        private readonly ApplicationDbContext _dbContext;
        private readonly TaskGroupDomainService _taskGroupDomainService;

        public bool ProjectRole { get; set; } // 项目管理员
        public int CurrentProjectId { get; set; }
        public Project Project { get; set; } = new();  // 所属项目
        public string CreatorId { get; set; } = string.Empty;  // 创建人
        public ApplicationUser Creator { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public DateTime? EndDate { get; set; }    // 结束时间
        [BindProperty(SupportsGet = true)]
        public string? Assignee { get; set; }     // 认领人

        public TaskGroup TaskGroup { get; set; } = new() { CreatorId = 0 };

        [BindProperty(SupportsGet = true)]
        public string? SearchTerm { get; set; }

        [BindProperty(SupportsGet = true)]
        public Entities.TaskStatus? Status { get; set; }  // 状态筛选

        [BindProperty(SupportsGet = true)]
        public TaskPriority? Priority { get; set; }       // 优先级筛选

        public List<SelectListItem> Assignees { get; set; } = new();

        public string DescriptionHtml => Markdown.ToHtml(
            TaskGroup.Description ?? string.Empty,
            new MarkdownPipelineBuilder().DisableHtml().UseAdvancedExtensions().Build());

        // 状态下拉列表：补齐 待审核、已取消，和会议任务状态统一
        public List<SelectListItem> StatusList { get; set; } = new()
        {
            new SelectListItem("全部", ""),
            new SelectListItem("未开始", ((int)Entities.TaskStatus.NotStarted).ToString()),
            new SelectListItem("进行中", ((int)Entities.TaskStatus.InProgress).ToString()),
            new SelectListItem("已完成", ((int)Entities.TaskStatus.Completed).ToString()),
            new SelectListItem("已取消", ((int)Entities.TaskStatus.Cancelled).ToString()),
            new SelectListItem("待审核", ((int)Entities.TaskStatus.PendingConfirmation).ToString())
        };

        // 优先级下拉列表
        public List<SelectListItem> PriorityList { get; set; } = new()
        {
            new SelectListItem("全部", ""),
            new SelectListItem("低", ((int)TaskPriority.Low).ToString()),
            new SelectListItem("中", ((int)TaskPriority.Medium).ToString()),
            new SelectListItem("高", ((int)TaskPriority.High).ToString())
        };

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

        public int CurrentPage { get; set; }
        public int TotalPages { get; set; }
        public IList<ToDoTask> Tasks { get; set; } = new List<ToDoTask>();

        public DetailsModel(ApplicationDbContext context, TaskGroupDomainService taskGroupDomainService)
        {
            _dbContext = context;
            _taskGroupDomainService = taskGroupDomainService;
        }

        public async Task<IActionResult> OnGetAsync(int? id, int? projectId, string? assignee, int pageIndex = 1)
        {
            if (id == null)
                return NotFound();

            var taskGroup = await _taskGroupDomainService.GetTaskGroupAsync(id);
            if (taskGroup == null) return NotFound();
            TaskGroup = taskGroup;

            if (!CurrentUserId.HasValue) return Challenge();
            if (!await CanAccessProjectAsync(TaskGroup.ProjectId, CurrentUserId.Value)) return Forbid();

            projectId = TaskGroup.ProjectId;
            CurrentProjectId = TaskGroup.ProjectId;

            ProjectRole = await CanManageProjectAsync(TaskGroup.ProjectId, CurrentUserId.Value);
            if (TaskGroup.IsDeleted && !ProjectRole) return NotFound();

            var query = _dbContext.ToDoTasks
                .AsQueryable()
                .Where(t => t.GroupId == id && !t.IsDeleted);

            // 标题模糊搜索
            if (!string.IsNullOrWhiteSpace(SearchTerm))
            {
                query = query.Where(t => t.Title.Contains(SearchTerm));
            }

            // 状态筛选
            if (Status.HasValue)
            {
                query = query.Where(t => t.Status == Status.Value);
            }

            // 优先级筛选
            if (Priority.HasValue)
            {
                query = query.Where(t => t.Priority == Priority.Value);
            }

            // 截止日期筛选
            if (EndDate.HasValue)
            {
                query = query.Where(t => t.EndTime <= EndDate.Value);
            }

            // 认领人筛选
            if (!string.IsNullOrWhiteSpace(assignee))
            {
                query = query.Where(t => t.Assignee != null && t.Assignee.UserName == assignee);
            }

            // 计算总页数
            int totalCount = await query.CountAsync();
            TotalPages = (int)Math.Ceiling(totalCount / (double)PageSize);
            CurrentPage = Math.Max(pageIndex, 1);

            // 分页查询数据
            Tasks = await query
                .OrderBy(t => t.Id)
                .Skip((CurrentPage - 1) * PageSize)
                .Take(PageSize)
                .ToListAsync();

            // 构建认领人下拉框，增加【全部】选项
            Assignees = new List<SelectListItem>
            {
                new SelectListItem("全部", "")
            };

            var userItems = await _dbContext.ProjectUsers
                .Where(member => member.ProjectId == TaskGroup.ProjectId && member.User != null && !member.User.IsDeleted)
                .Select(member => new SelectListItem
                {
                    Value = member.User!.UserName,
                    Text = member.User.UserName
                })
                .ToListAsync();

            Assignees.AddRange(userItems);

            return Page();
        }

        private async Task<bool> CanAccessProjectAsync(int projectId, int userId)
        {
            if (User.IsInRole(nameof(UserRole.systemAdmin))) return true;
            if (await _dbContext.Project.AsNoTracking().AnyAsync(project =>
                project.Id == projectId && !project.IsDeleted && project.LeaderUserId == userId)) return true;
            return await _dbContext.ProjectUsers.AsNoTracking().AnyAsync(member =>
                member.ProjectId == projectId && member.UserId == userId);
        }

        private async Task<bool> CanManageProjectAsync(int projectId, int userId)
        {
            if (User.IsInRole(nameof(UserRole.systemAdmin))) return true;
            if (await _dbContext.Project.AsNoTracking().AnyAsync(project =>
                project.Id == projectId && !project.IsDeleted && project.LeaderUserId == userId)) return true;
            return await _dbContext.ProjectUsers.AsNoTracking().AnyAsync(member =>
                member.ProjectId == projectId
                && member.UserId == userId
                && member.ProjectRole == (int)global::ProjectRole.Admin);
        }
    }
}

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Tasks
{
    public class SplitTaskModel : PageModel
    {
        private readonly ApplicationDbContext _dbcontext;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ToDoTaskDomainService _taskDomainService;
        private readonly IAIService _aiService;
        private readonly ILogger<SplitTaskModel> _logger;

        public SplitTaskModel(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            ToDoTaskDomainService taskDomainService,
            IAIService aiService,
            ILogger<SplitTaskModel> logger)
        {
            _dbcontext = context;
            _userManager = userManager;
            _taskDomainService = taskDomainService;
            _aiService = aiService;
            _logger = logger;
        }

        #region 核心属性
        public int ProjectId { get; set; }

        [Required(ErrorMessage = "请输入任务描述文本")]
        public string InputText { get; set; } = string.Empty;

        // 显式前缀防止首次拆分时字典绑定回退到整个表单，把 ProjectId 当成整数键。
        [BindProperty(Name = nameof(ProjectTaskGroups))]
        public Dictionary<int, ProjectTaskGroup> ProjectTaskGroups { get; set; } = new();
        [BindProperty] public Guid SubmissionId { get; set; }
        public string? WarningMessage { get; set; }
        public List<Project> AllProjectList { get; set; } = new();
        public SelectList GroupList { get; set; } = default!;
        public SelectList PriorityList { get; set; } = default!;
        public bool ShowResults { get; set; }
        public string? ErrorMessage { get; set; }
        #endregion

        #region 页面初始化
        public async Task<IActionResult> OnGetAsync(int? projectId)
        {
            if (!int.TryParse(_userManager.GetUserId(User), out var currentUserId)) return Challenge();
            ProjectId = projectId ?? 0;
            AllProjectList = await LoadAccessibleProjectsAsync(currentUserId);
            if (ProjectId > 0 && AllProjectList.All(project => project.Id != ProjectId)) return Forbid();

            await InitSelectListsAsync();
            return Page();
        }
        #endregion

        #region AI拆分核心方法（完全动态，无任何写死数据）
        public async Task<IActionResult> OnPostSplitAsync()
        {
            try
            {
                if (!int.TryParse(Request.Form["ProjectId"], out int projectId))
                {
                    projectId = 0;
                }
                ProjectId = projectId;

                if (!int.TryParse(_userManager.GetUserId(User), out var currentUserId)) return Challenge();
                AllProjectList = await LoadAccessibleProjectsAsync(currentUserId);
                if (ProjectId > 0 && AllProjectList.All(project => project.Id != ProjectId)) return Forbid();

                InputText = Request.Form["InputText"].ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(InputText))
                {
                    ErrorMessage = "请输入任务描述文本";
                    await InitSelectListsAsync();
                    return Page();
                }
                if (InputText.Length > 16000)
                {
                    ErrorMessage = "任务描述不能超过 16000 个字符";
                    await InitSelectListsAsync();
                    return Page();
                }

                var projectListStr = string.Join("\n", AllProjectList.Select(p => $"项目ID: {p.Id}, 项目名称: {p.Name}"));

                // 读取数据库真实分组（完全动态，无写死）
                var projectGroupDict = new Dictionary<int, List<TaskGroup>>();
                foreach (var p in AllProjectList)
                {
                    var groups = await _dbcontext.TaskGroups
                        .Where(g => g.ProjectId == p.Id && !g.IsDeleted)
                        .OrderBy(g => g.Name)
                        .ToListAsync();
                    projectGroupDict[p.Id] = groups;
                }

                var projectGroupInfo = new StringBuilder();
                foreach (var kv in projectGroupDict)
                {
                    int pid = kv.Key;
                    var project = AllProjectList.FirstOrDefault(p => p.Id == pid);
                    var groupNames = kv.Value.Select(g => g.Name).ToList();

                    projectGroupInfo.AppendLine($"【项目 {pid}：{project?.Name} 的可用分组】");
                    if (groupNames.Any())
                        projectGroupInfo.AppendLine(string.Join("、", groupNames));
                    else
                        projectGroupInfo.AppendLine("（该项目暂无分组）");
                    projectGroupInfo.AppendLine();
                }

                // 纯净动态 Prompt
                var prompt = $@"
你是一个任务分类专家，必须严格按以下规则处理：

【可用项目列表】
{projectListStr}

【各项目对应的可用分组（从数据库实时读取）】
{projectGroupInfo}

【处理规则（必须严格遵守）】
1. 每个任务必须先分配正确项目，再从该项目的分组里选择一个最匹配的
2. 只能使用上面列出的分组全名，不能自己创造、简写、修改
3. 无法确定项目时填 0
4. 无法确定分组时填空字符串 ""
5. 只返回标准JSON数组，无任何多余内容
6. 截止时间规则：只有文本中明确写出了具体截止日期的任务，deadline字段才填对应日期；没有明确写截止时间的任务，deadline字段必须返回空字符串""，绝对不能自己编造、补充任何日期！
7. 优先级规则：文本中明确写「高/优先」的填high，写「中」的填medium，写「低」的填low，没提的默认填medium

【返回JSON格式示例】
[
  {{
    ""title"": ""优化AI回答逻辑"",
    ""description"": ""提升AI回答准确率"",
    ""projectId"": 1,
    ""priority"": ""high"",
    ""group"": ""AI 问答优化组"",
    ""deadline"": ""2026-04-10""
  }},
  {{
    ""title"": ""修复bug"",
    ""description"": ""修复问题"",
    ""projectId"": 1,
    ""priority"": ""medium"",
    ""group"": ""问题修复组"",
    ""deadline"": """"
  }}
]

【用户输入文本】
{InputText}
";

                var aiResponse = await _aiService.GetChatCompletionAsync(prompt) ?? string.Empty;
                _logger.LogDebug("AI task split returned {ResponseLength} characters", aiResponse.Length);

                if (!TaskSplitJsonParser.TryParse(aiResponse, out var jsonItems, out var wasTruncated))
                {
                    ErrorMessage = "AI返回格式错误，未找到有效JSON数组";
                    await InitSelectListsAsync();
                    return Page();
                }

                var aiItems = jsonItems.Deserialize<List<AIParseItem>>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (aiItems == null || !aiItems.Any())
                {
                    ErrorMessage = "AI未识别到有效任务";
                    await InitSelectListsAsync();
                    return Page();
                }

                ProjectTaskGroups.Clear();
                ModelState.Clear();
                SubmissionId = Guid.NewGuid();
                if (wasTruncated) WarningMessage = "AI 输出被截断，已保留完整任务。请核对是否有遗漏，必要时分段重新拆分。";

                foreach (var item in aiItems)
                {
                    int projectIdItem = item.ProjectId ?? 0;
                    var project = AllProjectList.FirstOrDefault(p => p.Id == projectIdItem);
                    if (project == null) projectIdItem = 0;
                    int fixedGroupKey = projectIdItem == 0 ? 0 : projectIdItem;

                    if (!ProjectTaskGroups.ContainsKey(fixedGroupKey))
                    {
                        ProjectTaskGroups[fixedGroupKey] = new ProjectTaskGroup
                        {
                            ProjectId = fixedGroupKey,
                            ProjectName = project?.Name ?? "未分配项目",
                            Tasks = new List<EditableSubTask>()
                        };
                    }

                    var priority = ParsePriority(item.Priority);
                    DateTime? deadline = null;
                    if (!string.IsNullOrWhiteSpace(item.Deadline) && DateTime.TryParse(item.Deadline, out var parsedDate))
                    {
                        deadline = parsedDate;
                    }

                    int? groupId = null;
                    if (projectIdItem > 0 && !string.IsNullOrWhiteSpace(item.Group))
                    {
                        string cleanInputGroup = item.Group.Replace(" ", "").ToLower().Trim();

                        var group = await _dbcontext.TaskGroups
                            .FirstOrDefaultAsync(g =>
                                g.ProjectId == projectIdItem &&
                                !g.IsDeleted &&
                                g.Name.Replace(" ", "").ToLower().Trim() == cleanInputGroup);

                        if (group != null)
                        {
                            groupId = group.Id;
                        }
                    }

                    ProjectTaskGroups[fixedGroupKey].Tasks.Add(new EditableSubTask
                    {
                        Title = item.Title ?? string.Empty,
                        Description = item.Description ?? string.Empty,
                        ProjectId = projectIdItem,
                        Priority = priority,
                        Deadline = deadline,
                        GroupId = groupId,
                        IsDeleted = false
                    });
                }

                ShowResults = true;
                await InitSelectListsAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI拆分任务失败");
                ErrorMessage = "AI拆分失败，请稍后重试";
                await InitSelectListsAsync();
                return Page();
            }
        }
        #endregion

        #region 保存所有任务
        public async Task<IActionResult> OnPostSaveAllAsync()
        {
            if (!int.TryParse(Request.Form["ProjectId"], out int pageProjectId))
                pageProjectId = 0;
            ProjectId = pageProjectId;

            var userIdStr = _userManager.GetUserId(User);
            if (!int.TryParse(userIdStr, out int currentUserId))
            {
                TempData["Message"] = "用户身份无效，请重新登录";
                ShowResults = true;
                await InitSelectListsAsync();
                return Page();
            }
            AllProjectList = await LoadAccessibleProjectsAsync(currentUserId);
            var accessibleProjectIds = AllProjectList.Select(project => project.Id).ToHashSet();
            if (SubmissionId == Guid.Empty)
            {
                ErrorMessage = "拆分批次已失效，请重新执行 AI 拆分后保存";
                RestorePostedProjectNames();
                ShowResults = true;
                await InitSelectListsAsync();
                return Page();
            }

            var tasksToSave = new List<EditableSubTask>();
            bool hasError = false;
            ErrorMessage = null;

            foreach (var key in Request.Form.Keys)
            {
                if (key.StartsWith("ProjectTaskGroups[") && key.Contains(".Tasks[") && key.EndsWith("].Title"))
                {
                    try
                    {
                        string[] parts = key.Split('[', ']');
                        int groupKey = int.Parse(parts[1]);
                        int taskIndex = int.Parse(parts[3]);

                        string title = Request.Form[$"ProjectTaskGroups[{groupKey}].Tasks[{taskIndex}].Title"].ToString();
                        string desc = Request.Form[$"ProjectTaskGroups[{groupKey}].Tasks[{taskIndex}].Description"].ToString();
                        string deadlineStr = Request.Form[$"ProjectTaskGroups[{groupKey}].Tasks[{taskIndex}].Deadline"].ToString();
                        string priorityStr = Request.Form[$"ProjectTaskGroups[{groupKey}].Tasks[{taskIndex}].Priority"].ToString();
                        string groupIdStr = Request.Form[$"ProjectTaskGroups[{groupKey}].Tasks[{taskIndex}].GroupId"].ToString();
                        string isDeletedStr = Request.Form[$"ProjectTaskGroups[{groupKey}].Tasks[{taskIndex}].IsDeleted"].ToString();
                        string taskProjectIdStr = Request.Form[$"ProjectTaskGroups[{groupKey}].Tasks[{taskIndex}].ProjectId"].ToString();

                        bool isDeleted = bool.TryParse(isDeletedStr, out var d) && d;
                        if (isDeleted)
                            continue;
                        if (string.IsNullOrWhiteSpace(title))
                        {
                            ErrorMessage = $"❌ 第 {taskIndex + 1} 条任务：任务名称不能为空";
                            hasError = true;
                            continue;
                        }

                        if (!int.TryParse(taskProjectIdStr, out int realProjectId) || realProjectId <= 0)
                        {
                            ErrorMessage = $"❌ 第 {taskIndex + 1} 条任务：请选择所属项目";
                            hasError = true;
                            continue;
                        }
                        if (!accessibleProjectIds.Contains(realProjectId))
                        {
                            return Forbid();
                        }

                        int? groupId = null;
                        if (!string.IsNullOrWhiteSpace(groupIdStr))
                        {
                            if (!int.TryParse(groupIdStr, out var parsedGroupId) || parsedGroupId <= 0)
                            {
                                ErrorMessage = $"❌ 第 {taskIndex + 1} 条任务：任务分组无效";
                                hasError = true;
                                continue;
                            }
                            if (!await _dbcontext.TaskGroups.AsNoTracking().AnyAsync(group =>
                                group.Id == parsedGroupId && group.ProjectId == realProjectId && !group.IsDeleted))
                            {
                                ErrorMessage = $"❌ 第 {taskIndex + 1} 条任务：任务分组不属于所选项目";
                                hasError = true;
                                continue;
                            }
                            groupId = parsedGroupId;
                        }
                        title = title.Trim();
                        if (title.Length > 255)
                        {
                            ErrorMessage = $"❌ 第 {taskIndex + 1} 条任务：标题不能超过 255 个字符";
                            hasError = true;
                            continue;
                        }
                        if (desc.Length > 16_000)
                        {
                            ErrorMessage = $"❌ 第 {taskIndex + 1} 条任务：描述不能超过 16000 个字符";
                            hasError = true;
                            continue;
                        }

                        DateTime? deadline = DateTime.TryParse(deadlineStr, out var dd) ? dd : null;
                        var priority = Enum.TryParse<TaskPriority>(priorityStr, true, out var parsedPriority)
                            && Enum.IsDefined(parsedPriority)
                            ? parsedPriority
                            : TaskPriority.Medium;

                        tasksToSave.Add(new EditableSubTask
                        {
                            Title = title,
                            Description = desc,
                            ProjectId = realProjectId,
                            Deadline = deadline,
                            Priority = priority,
                            GroupId = groupId,
                            IsDeleted = false
                        });
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            if (hasError)
            {
                RestorePostedProjectNames();
                ShowResults = true;
                await InitSelectListsAsync();
                return Page();
            }

            if (tasksToSave.Count == 0)
            {
                RestorePostedProjectNames();
                TempData["Message"] = "⚠ 没有可保存的有效任务";
                ShowResults = true;
                await InitSelectListsAsync();
                return Page();
            }

            var newTasks = new List<ToDoTask>();
            foreach (var subTask in tasksToSave)
            {
                var newTask = new ToDoTask
                {
                    Title = subTask.Title,
                    Description = subTask.Description,
                    ProjectId = subTask.ProjectId.GetValueOrDefault(),
                    Priority = subTask.Priority,
                    Status = Entities.TaskStatus.NotStarted,
                    GroupId = subTask.GroupId,
                    EndTime = subTask.Deadline,
                    CreatorId = currentUserId,
                    CreatedAt = AppTime.Now,
                    UpdatedAt = AppTime.Now
                };

                newTasks.Add(newTask);
            }
            var saveCount = await new TaskSplitSubmissionService(_dbcontext).SaveAsync(currentUserId, SubmissionId, newTasks);

            TempData["Message"] = $"✅ 保存成功！已添加 {saveCount} 个任务";
            return RedirectToPage("/Tasks/Index", new { projectId = ProjectId > 0 ? (int?)ProjectId : null });
        }
        #endregion

        #region 获取项目下分组
        public async Task<IActionResult> OnGetGroupsByProject(int projectId)
        {
            if (!int.TryParse(_userManager.GetUserId(User), out var currentUserId)) return Unauthorized();
            if (!await CanAccessProjectAsync(projectId, currentUserId)) return Forbid();
            var groups = await _dbcontext.TaskGroups
                .Where(g => g.ProjectId == projectId && !g.IsDeleted)
                .OrderBy(g => g.Name)
                .Select(g => new { id = g.Id, name = g.Name })
                .ToListAsync();

            return new JsonResult(groups);
        }
        #endregion

        #region 辅助方法
        private async Task InitSelectListsAsync()
        {
            if (!int.TryParse(_userManager.GetUserId(User), out var currentUserId))
            {
                GroupList = new SelectList(Array.Empty<TaskGroup>(), "Id", "Name");
                PriorityList = new SelectList(Array.Empty<SelectListItem>());
                return;
            }
            var accessibleProjectIds = (await LoadAccessibleProjectsAsync(currentUserId))
                .Select(project => project.Id)
                .ToList();
            var groups = await _dbcontext.TaskGroups
                .Where(group => accessibleProjectIds.Contains(group.ProjectId) && !group.IsDeleted)
                .OrderBy(g => g.Name)
                .ToListAsync();
            GroupList = new SelectList(groups, "Id", "Name");

            PriorityList = new SelectList(
                Enum.GetValues(typeof(TaskPriority)).Cast<TaskPriority>()
                    .Select(p => new SelectListItem
                    {
                        Value = p.ToString(),
                        Text = p switch
                        {
                            TaskPriority.Low => "低",
                            TaskPriority.Medium => "中",
                            TaskPriority.High => "高",
                            _ => p.ToString()
                        }
                    }),
                "Value", "Text");
        }

        private async Task<List<Project>> LoadAccessibleProjectsAsync(int userId)
        {
            var query = _dbcontext.Project.AsNoTracking()
                .Where(project => !project.IsDeleted && project.Status == ProjectStatus.Active);
            if (!User.IsInRole(nameof(UserRole.systemAdmin)))
            {
                query = query.Where(project => project.LeaderUserId == userId
                    || _dbcontext.ProjectUsers.Any(member => member.ProjectId == project.Id && member.UserId == userId));
            }
            return await query.OrderBy(project => project.Name).ToListAsync();
        }

        private async Task<bool> CanAccessProjectAsync(int projectId, int userId)
        {
            if (User.IsInRole(nameof(UserRole.systemAdmin)))
                return await _dbcontext.Project.AsNoTracking().AnyAsync(project =>
                    project.Id == projectId && !project.IsDeleted);
            return await _dbcontext.Project.AsNoTracking().AnyAsync(project =>
                project.Id == projectId
                && !project.IsDeleted
                && (project.LeaderUserId == userId
                    || _dbcontext.ProjectUsers.Any(member => member.ProjectId == project.Id && member.UserId == userId)));
        }

        private TaskPriority ParsePriority(string? priority)
        {
            return priority?.ToLower() switch
            {
                "high" => TaskPriority.High,
                "low" => TaskPriority.Low,
                _ => TaskPriority.Medium
            };
        }

        private void RestorePostedProjectNames()
        {
            foreach (var (key, group) in ProjectTaskGroups)
            {
                group.ProjectId = key;
                group.ProjectName = AllProjectList.FirstOrDefault(project => project.Id == key)?.Name ?? "未分配项目";
            }
        }
        #endregion

        #region 模型
        public class ProjectTaskGroup
        {
            public int ProjectId { get; set; }
            public string ProjectName { get; set; } = string.Empty;
            public List<EditableSubTask> Tasks { get; set; } = new();
        }

        public class EditableSubTask
        {
            [Required(ErrorMessage = "任务名称不能为空")]
            public string Title { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            [Required(ErrorMessage = "请选择项目")]
            public int? ProjectId { get; set; }
            public DateTime? Deadline { get; set; }
            public TaskPriority Priority { get; set; } = TaskPriority.Medium;
            public int? GroupId { get; set; }
            public bool IsDeleted { get; set; }
        }

        private class AIParseItem
        {
            public string? Title { get; set; }
            public string? Description { get; set; }
            public int? ProjectId { get; set; }
            public string? Priority { get; set; }
            public string? Group { get; set; }
            public string? Deadline { get; set; }
        }
        #endregion
    }
}

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ToDo.Context;
using ToDo.Entities;
using ToDo.Entities.Dto;
using ToDo.Razor;
using static ToDo.Domain.OperationLogService;

namespace ToDo.Domain
{

    public class ToDoTaskDomainService
    {
        private readonly ApplicationDbContext _dbcontext;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<ProjectDomain> _logger;
        public ToDoTaskDomainService(ApplicationDbContext context, UserManager<ApplicationUser> userManager, ILogger<ProjectDomain> logger)
        {
            _dbcontext = context;
            _userManager = userManager;
            _logger = logger;
        }
        /// <summary>获取项目下所有可用任务分组，用于下拉框</summary>
        public async Task<List<DomainSelectListItem>> GetProjectTaskGroupSelectListAsync(int projectId)
        {
            return await _dbcontext.TaskGroups
                .Where(g => g.ProjectId == projectId && !g.IsDeleted)
                .OrderBy(g => g.Name)
                .Select(g => new DomainSelectListItem(g.Name, g.Id.ToString()))
                .ToListAsync();
        }
        /// <summary>
        /// 批量创建子任务
        /// </summary>
        public async Task<bool> BatchCreateSubTasksAsync(CreateSubTasksDto createDto, int currentUserId)
        {
            if (createDto == null || createDto.SubTaskTitles == null || !createDto.SubTaskTitles.Any())
                return false;

            try
            {
                // 获取父任务
                var parentTask = await _dbcontext.ToDoTasks
                    .FirstOrDefaultAsync(t => t.Id == createDto.ParentTaskId && !t.IsDeleted);

                if (parentTask == null)
                    return false;

                foreach (var title in createDto.SubTaskTitles)
                {
                    if (string.IsNullOrWhiteSpace(title))
                        continue;

                    var subTask = new ToDoTask
                    {
                        Title = title.Trim(),
                        Description = $"由任务「{parentTask.Title}」拆分生成",
                        ProjectId = parentTask.ProjectId,
                        GroupId = createDto.GroupId ?? parentTask.GroupId,
                        Priority = createDto.Priority,
                        Status = Entities.TaskStatus.NotStarted,
                        CreatorId = currentUserId,
                        CreatedAt = AppTime.Now,
                        UpdatedAt = AppTime.Now
                    };

                    _dbcontext.ToDoTasks.Add(subTask);
                    await _dbcontext.SaveChangesAsync();

                    // 记录日志
                    await LogTaskOperationAsync(
                        operationType: OperationType.创建,
                        target: OperationTarget.任务,
                        targetId: subTask.Id,
                        operatorUserId: currentUserId,
                        projectId: subTask.ProjectId,
                        taskId: subTask.Id,
                        taskTitle: subTask.Title,
                        taskName: subTask.Title,
                        afterState: $"标题: {subTask.Title};\n优先级: {subTask.Priority}",
                        status: OperationStatus.成功
                    );
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量创建子任务失败，父任务ID: {ParentTaskId}", createDto.ParentTaskId);
                return false;
            }
        }
        // 修改详情列表
        public class ChangeDetail
        {
            public string FieldName { get; set; } = "";
            public string BeforeValue { get; set; } = "";
            public string AfterValue { get; set; } = "";
        }

        // 获取任务
        public async Task<ToDoTask?> GetTaskByIdAsync(int taskId)
        {
            return await _dbcontext.ToDoTasks
                .Include(t => t.Project)
                .Include(t => t.Assignee)
                .Include(t => t.AgentDefinition)
                .Include(t => t.Reviewer)
                .Include(t => t.ParentTask)
                .Include(t => t.SubTasks.Where(child => !child.IsDeleted))
                .Include(t => t.Comments.Where(comment => true))
                    .ThenInclude(comment => comment.Author)
                .Include(t => t.LabelLinks)
                    .ThenInclude(link => link.Label)
                .FirstOrDefaultAsync(t => t.Id == taskId);
        }

        // 获取用户列表
        public async Task<List<string>> GetDistinctUsersAsync()
        {
            return await _dbcontext.ChangeLogs
                .Where(log => !string.IsNullOrEmpty(log.OperatedByRealName))
                .Select(log => log.OperatedByRealName!)
                .Distinct()
                .OrderBy(name => name)
                .ToListAsync();
        }

        // 查询参数模型
        public class TaskDetailsLogQueryParameters
        {
            public int TaskId { get; set; }
            public string UserName { get; set; } = string.Empty;
            public string ProjectName { get; set; } = string.Empty;
            public OperationTarget? OperationTarget { get; set; }
            public OperationType? OperationType { get; set; }
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
            public int PageIndex { get; set; } = 1;
            public int PageSize { get; set; } = 10;
        }

        // 分页获取日志
        public async Task<PaginatedList<ChangeLog>> GetPagedLogsAsync(int taskId, TaskDetailsLogQueryParameters parameters)
        {
            // 验证日期
            ValidateDateRange(parameters);

            var query = _dbcontext.ChangeLogs
                .Include(c => c.OperatedByUser)
                .Where(c => c.TaskId == taskId)
                .AsQueryable();

            //if (!string.IsNullOrEmpty(parameters.UserName))
            //    query = query.Where(c => c.OperatedByRealName.Contains(parameters.UserName));
            if (!string.IsNullOrEmpty(parameters.UserName))
            {
                if (int.TryParse(parameters.UserName, out int userId))
                    query = query.Where(c => c.OperatedByUserId == userId);
            }
            if (!string.IsNullOrEmpty(parameters.ProjectName))
                query = query.Where(c => c.ProjectName != null && c.ProjectName.Contains(parameters.ProjectName));
            if (parameters.OperationTarget.HasValue)
                query = query.Where(c => c.OperationTarget == parameters.OperationTarget.Value);
            if (parameters.OperationType.HasValue)
                query = query.Where(c => c.OperationType == parameters.OperationType.Value);
            if (parameters.StartDate.HasValue)
            {
                var startDateUtc = AppTime.ToUtc(parameters.StartDate.Value.Date);
                query = query.Where(c => c.OperatedAt >= startDateUtc);
            }
            if (parameters.EndDate.HasValue)
            {
                var endDateUtc = AppTime.ToUtc(parameters.EndDate.Value.Date.AddDays(1).AddTicks(-1));
                query = query.Where(c => c.OperatedAt <= endDateUtc);
            }

            query = query.OrderByDescending(c => c.OperatedAt);

            return await PaginatedList<ChangeLog>.CreateAsync(query.AsNoTracking(), parameters.PageIndex, parameters.PageSize);
        }

        // 验证日期范围
        private void ValidateDateRange(TaskDetailsLogQueryParameters parameters)
        {
            var today = AppTime.Today;

            if (parameters.StartDate.HasValue && parameters.StartDate.Value.Date > today)
                parameters.StartDate = today;

            if (parameters.EndDate.HasValue && parameters.EndDate.Value.Date > today)
                parameters.EndDate = today;

            if (parameters.StartDate.HasValue && parameters.EndDate.HasValue && parameters.EndDate < parameters.StartDate)
                parameters.EndDate = parameters.StartDate;
        }

        /// <summary>
        /// 比较两个对象的指定属性，生成“字段: 原值 → 新值”文本
        /// </summary>
        public async Task<List<ChangeDetail>> GenerateChangeSummaryAsync(ToDoTask original, ToDoTask updated)
        {
            var changes = new List<ChangeDetail>();

            var fieldNames = new Dictionary<string, string>
    {
        {"Title", "标题"},
        {"Description", "描述"},
        {"AssigneeId", "负责人"},
        {"ClaimerId", "负责人"},
        {"EndTime", "截止时间"},
        {"Status", "状态"},
        {"Priority", "优先级"},
        {"ReviewerId", "审核人"},
        {"ParentTaskId", "父任务"},
        {"AssigneeType", "执行主体"},
        {"AgentName", "数字员工"}
    };

            foreach (var kv in fieldNames)
            {
                var prop = typeof(ToDoTask).GetProperty(kv.Key);
                if (prop == null) continue;

                object? originalValue = prop.GetValue(original);
                object? updatedValue = prop.GetValue(updated);

                // 枚举值中文
                string GetDisplayName(object? value)
                {
                    if (value == null) return "未指定";

                    var type = value.GetType();
                    if (!type.IsEnum) return value.ToString() ?? "null";

                    var member = type.GetMember(value.ToString()!);
                    var displayAttr = member[0].GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.DisplayAttribute), false)
                                               .FirstOrDefault() as System.ComponentModel.DataAnnotations.DisplayAttribute;
                    return displayAttr?.Name ?? value.ToString()!;
                }

                // 负责人显示用户名
                if (kv.Key == "AssigneeId" || kv.Key == "ClaimerId")
                {
                    originalValue = originalValue == null ? "未指定" : (await _dbcontext.Users.FindAsync((int)originalValue))?.UserName ?? "未知用户";
                    updatedValue = updatedValue == null ? "未指定" : (await _dbcontext.Users.FindAsync((int)updatedValue))?.UserName ?? "未知用户";
                }
                else if (prop.PropertyType.IsEnum)
                {
                    originalValue = GetDisplayName(originalValue);
                    updatedValue = GetDisplayName(updatedValue);
                }

                // 日期格式化
                if (originalValue is DateTime dt1) originalValue = dt1.ToString("yyyy-MM-dd HH:mm");
                if (updatedValue is DateTime dt2) updatedValue = dt2.ToString("yyyy-MM-dd HH:mm");

                if ((originalValue?.ToString() ?? "") != (updatedValue?.ToString() ?? ""))
                {
                    changes.Add(new ChangeDetail
                    {
                        FieldName = kv.Value, // 中文名
                        BeforeValue = originalValue?.ToString() ?? "",
                        AfterValue = updatedValue?.ToString() ?? ""
                    });
                }
            }

            return changes;
        }

        /// <summary>
        /// 通用日志记录方法：适用于项目/任务/日报/会议纪要等
        /// 确保操作日志完整性和事务一致性
        /// </summary>
        public virtual async Task LogTaskOperationAsync(
            OperationType operationType,
            OperationTarget target,
            int operatorUserId,
            string? beforeState = null,
            string? afterState = null,
            OperationStatus status = OperationStatus.成功,
            int? projectId = null,
            string? projectName = null,
            int? taskId = null,
            string? taskTitle = null,
            string? taskName = null,
            int? targetId = null,
            string? targetName = null)
        {
            try
            {
                // 获取操作人信息
                var operatorInfo = await _dbcontext.Users
                    .Where(u => u.Id == operatorUserId)
                    .Select(u => new
                    {
                        UserName = u.UserName ?? "unknown",
                        RealName = u.RealName ?? u.UserName ?? "未知用户"
                    })
                    .FirstOrDefaultAsync();

                //if (operatorInfo == null)
                //{
                //    _logger.LogWarning("操作人ID {UserId} 不存在，使用默认信息记录日志", operatorUserId);
                //    operatorInfo = new { UserName = "unknown", RealName = "未知用户" };
                //}

                // 如果提供了 projectId 且 projectName 为空，尝试查询项目名
                if (projectId.HasValue && string.IsNullOrEmpty(projectName))
                {
                    projectName = await _dbcontext.Project
                        .Where(p => p.Id == projectId.Value)
                        .Select(p => p.Name)
                        .FirstOrDefaultAsync() ?? "未知项目";
                }

                // 构造 ChangeLog
                var logEntry = new ChangeLog
                {
                    OperationType = operationType,
                    OperationStatus = status,
                    OperatedAt = DateTime.UtcNow,
                    OperationTarget = target,
                    OperatedByUserId = operatorUserId,
                    OperatedByUserName = operatorInfo?.UserName ?? "unknown",
                    OperatedByRealName = operatorInfo?.RealName ?? "未知用户",
                    ProjectId = projectId,
                    ProjectName = projectName,
                    TaskId = taskId,
                    TaskTitle = taskTitle,
                    TaskName = taskName,
                    TargetId = targetId,
                    TargetName = targetName,
                    BeforeContent = beforeState,
                    AfterContent = afterState
                };

                _dbcontext.ChangeLogs.Add(logEntry);
                await _dbcontext.SaveChangesAsync();

                _logger.LogDebug("操作日志记录成功：{OperationType} {Target} {TargetId}，操作人 {UserId}",
                    operationType, target, targetId ?? taskId ?? projectId, operatorUserId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "操作日志记录失败：操作类型 {OperationType}，目标 {Target} {TargetId}",
                    operationType, target, targetId ?? taskId ?? projectId);
            }
        }

       
    }
}

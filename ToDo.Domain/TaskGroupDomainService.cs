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
using static ToDo.Domain.ToDoTaskDomainService;

namespace ToDo.Domain
{
    public class TaskGroupDomainService
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ILogger<ProjectDomain> _logger;
        public TaskGroupDomainService(ApplicationDbContext context, ILogger<ProjectDomain> logger)
        {
            _dbContext = context;
            _logger = logger;
        }

        // 获取分组
        public async Task<TaskGroup?> GetTaskGroupAsync(int? taskGroupId)
        {
            return await _dbContext.TaskGroups
                .Include(g => g.Project)                 // 加载所属项目
                .Include(g => g.Creator)                 // 加载创建人
                .Include(g => g.Tasks)                   // 显示任务列表
                    .ThenInclude(t => t.Assignee)       // 任务指派人
                .FirstOrDefaultAsync(g => g.Id == taskGroupId);
        }

        /// <summary>
        /// 比较两个对象的指定属性，生成“字段: 原值 → 新值”文本
        /// </summary>
        public async Task<List<ChangeDetail>> GenerateGroupChangeSummaryAsync(TaskGroup original, TaskGroup updated)
        {
            var changes = new List<ChangeDetail>();

            var fieldNames = new Dictionary<string, string>
    {
        { "Name", "分组名称" },
        { "Description", "分组描述" },
        { "ProjectId", "所属项目" },
        { "CreatedId", "创建人" } // 如果需要，可以加上
    };

            foreach (var kv in fieldNames)
            {
                var prop = typeof(TaskGroup).GetProperty(kv.Key);
                if (prop == null) continue;

                object? originalValue = prop.GetValue(original);
                object? updatedValue = prop.GetValue(updated);

                // 枚举值转中文
                string GetDisplayName(object? value)
                {
                    if (value == null) return "未指定";

                    var type = value.GetType();
                    if (!type.IsEnum) return value.ToString() ?? "null";

                    var member = type.GetMember(value.ToString()!);
                    var displayAttr = member[0]
                        .GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.DisplayAttribute), false)
                        .FirstOrDefault() as System.ComponentModel.DataAnnotations.DisplayAttribute;

                    return displayAttr?.Name ?? value.ToString()!;
                }

                // ProjectId 显示项目名称
                if (kv.Key == "ProjectId")
                {
                    originalValue = originalValue == null ? "未指定" : (await _dbContext.Project.FindAsync((int)originalValue))?.Name ?? "未知项目";
                    updatedValue = updatedValue == null ? "未指定" : (await _dbContext.Project.FindAsync((int)updatedValue))?.Name ?? "未知项目";
                }
                // CreatedId 显示用户名
                else if (kv.Key == "CreatedId")
                {
                    originalValue = originalValue == null ? "未指定" : (await _dbContext.Users.FindAsync((int)originalValue))?.UserName ?? "未知用户";
                    updatedValue = updatedValue == null ? "未指定" : (await _dbContext.Users.FindAsync((int)updatedValue))?.UserName ?? "未知用户";
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
                        FieldName = kv.Value,
                        BeforeValue = originalValue?.ToString() ?? "",
                        AfterValue = updatedValue?.ToString() ?? ""
                    });
                }
            }

            return changes;
        }

        /// <summary>
        /// 记录分组操作日志
        /// </summary>
        /// <param name="taskGroupId">分组ID</param>
        /// <param name="operation">操作类型 (Create/Edit/Delete/Restore)</param>
        /// <param name="operatorId">操作人ID</param>
        /// <param name="changedFields">变动的字段（编辑时可传入）</param>
        public virtual async Task LogTaskGroupOperationAsync(
    OperationType operationType,
    OperationTarget target,
    int operatorUserId,
    string? beforeState = null,
    string? afterState = null,
    OperationStatus status = OperationStatus.成功,
    int? projectId = null,
    string? projectName = null,
    int? targetId = null,
    string? targetName = null)
        {
            try
            {
                // 操作人信息
                var operatorInfo = await _dbContext.Users
                    .Where(u => u.Id == operatorUserId)
                    .Select(u => new { u.UserName, u.RealName })
                    .FirstOrDefaultAsync();

                var userName = operatorInfo?.UserName ?? "未知用户";
                var realName = operatorInfo?.RealName ?? "未知姓名";

                // 项目信息
                if (projectId.HasValue && string.IsNullOrEmpty(projectName))
                {
                    projectName = await _dbContext.Project
                        .Where(p => p.Id == projectId.Value)
                        .Select(p => p.Name)
                        .FirstOrDefaultAsync() ?? "未知项目";
                }

                // 构造日志
                var logEntry = new ChangeLog
                {
                    OperationType = operationType,
                    OperationStatus = status,
                    OperatedAt = AppTime.Now, // 保持和系统一致
                    OperationTarget = target,
                    OperatedByUserId = operatorUserId,
                    OperatedByUserName = userName,
                    OperatedByRealName = realName,
                    ProjectId = projectId,
                    ProjectName = projectName,
                    TargetId = targetId,
                    TargetName = targetName,
                    BeforeContent = beforeState,
                    AfterContent = afterState
                };

                _dbContext.ChangeLogs.Add(logEntry);
                await _dbContext.SaveChangesAsync();

                _logger.LogDebug("操作日志记录成功：{OperationType} {Target} {TargetId}，操作人 {UserId}",
                    operationType, target, targetId ?? projectId, operatorUserId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "操作日志记录失败：操作类型 {OperationType}，目标 {Target} {TargetId}",
                    operationType, target, targetId ?? projectId);
                throw; 
            }
        }
        public async Task<bool> IsAdminAsync(int projectId, int userId)
        {
            return await _dbContext.ProjectUsers
                .AnyAsync(pu => pu.ProjectId == projectId
                             && pu.UserId == userId
                             && pu.ProjectRole == 0); // 0 = Admin
        }
    }
}

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ToDo.Context;
using ToDo.Entities;
using ToDo.Razor;
using ToDo.Razor.Data;

namespace ToDo.Domain
{
    public class OperationLogService
    {
        private readonly ApplicationDbContext _context;

        public OperationLogService(ApplicationDbContext context)
        {
            _context = context;
        }

        // === 参数模型 ===
        public class LogQueryParameters
        {
            public string UserName { get; set; } = string.Empty;
            public string ProjectName { get; set; } = string.Empty;
            public OperationTarget? OperationTarget { get; set; }
            public OperationType? OperationType { get; set; }
            public int? TaskId { get; set; }
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
            public int PageIndex { get; set; } = 1;
            public int PageSize { get; set; } = 10;
        }

        // === 权限验证 ===
        public async Task<bool> CanViewLogsAsync(ApplicationUser user)
        {
            return user.Role == UserRole.systemAdmin ||
                   await _context.Project.AnyAsync(project => !project.IsDeleted
                       && (project.LeaderUserId == user.Id
                           || _context.ProjectUsers.Any(member => member.ProjectId == project.Id && member.UserId == user.Id)));
        }

        // === 获取用户和项目列表 ===
        public async Task<List<string>> GetDistinctUsersAsync(ApplicationUser user)
        {
            return await ApplyAccess(_context.ChangeLogs, user)
                .Where(log => !string.IsNullOrEmpty(log.OperatedByRealName))
                .Select(log => log.OperatedByRealName!)
                .Distinct()
                .OrderBy(name => name)
                .ToListAsync();
        }

        public async Task<List<string>> GetDistinctProjectsAsync(ApplicationUser user)
        {
            return await ApplyAccess(_context.ChangeLogs, user)
                .Where(log => !string.IsNullOrEmpty(log.ProjectName))
                .Select(log => log.ProjectName!)
                .Distinct()
                .OrderBy(name => name)
                .ToListAsync();
        }

        public async Task<ToDoTask?> GetTaskDetailsAsync(int? taskId, ApplicationUser user)
        {
            var query = _context.ToDoTasks
            .Include(t => t.Project)
            .Include(t => t.Assignee)
            .Where(task => task.Id == taskId);
            if (user.Role != UserRole.systemAdmin)
            {
                query = query.Where(task => task.Project != null && !task.Project.IsDeleted
                    && (task.Project.LeaderUserId == user.Id
                        || _context.ProjectUsers.Any(member => member.ProjectId == task.ProjectId && member.UserId == user.Id)));
            }
            return await query.FirstOrDefaultAsync();
        }

        // === 核心查询 ===
        public async Task<PaginatedList<ChangeLog>> GetPagedLogsAsync(LogQueryParameters parameters, ApplicationUser user)
        {
            ValidateDateRange(parameters);
            parameters.PageIndex = Math.Max(1, parameters.PageIndex);
            parameters.PageSize = Math.Clamp(parameters.PageSize, 1, 100);
            var query = BuildBaseQuery(parameters, user);
            return await PaginatedList<ChangeLog>.CreateAsync(query.AsNoTracking(), parameters.PageIndex, parameters.PageSize);
        }

        public async Task<(ChangeLog Log, int Index, string BeforeContent, string AfterContent)?>
            GetLogDetailsAsync(int id, LogQueryParameters parameters, ApplicationUser user)
        {
            var log = await ApplyAccess(_context.ChangeLogs, user)
                .Include(c => c.OperatedByUser)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (log == null) return null;

            var query = BuildBaseQuery(parameters, user);
            var logs = await query.OrderByDescending(c => c.OperatedAt).ToListAsync();
            int index = logs.FindIndex(c => c.Id == id) + 1;

            return (log, index, FormatContent(log.BeforeContent), FormatContent(log.AfterContent));
        }

        // === 私有方法 ===
        private IQueryable<ChangeLog> BuildBaseQuery(LogQueryParameters parameters, ApplicationUser user)
        {
            var query = ApplyAccess(_context.ChangeLogs.Include(c => c.OperatedByUser), user);

            if (!string.IsNullOrEmpty(parameters.UserName))
            {
                query = query.Where(c => c.OperatedByRealName == parameters.UserName);
            }

            if (!string.IsNullOrEmpty(parameters.ProjectName))
            {
                query = query.Where(c => c.ProjectName == parameters.ProjectName);
            }

            if (parameters.OperationTarget.HasValue)
            {
                query = query.Where(c => c.OperationTarget == parameters.OperationTarget.Value);
            }

            if (parameters.OperationType.HasValue)
            {
                query = query.Where(c => c.OperationType == parameters.OperationType.Value);
            }

            if (parameters.TaskId.HasValue)
            {
                query = query.Where(c => c.TaskId == parameters.TaskId.Value);
            }

            if (parameters.StartDate.HasValue)
            {
                // 用户输入的是北京自然日，显式转换为 UTC，不能依赖服务器操作系统时区。
                var startDateUtc = AppTime.ToUtc(parameters.StartDate.Value.Date);
                query = query.Where(c => c.OperatedAt >= startDateUtc);
            }

            if (parameters.EndDate.HasValue)
            {
                var endDateUtc = AppTime.ToUtc(parameters.EndDate.Value.Date.AddDays(1).AddTicks(-1));
                query = query.Where(c => c.OperatedAt <= endDateUtc);
            }

            return query.OrderByDescending(c => c.OperatedAt);
        }

        private IQueryable<ChangeLog> ApplyAccess(IQueryable<ChangeLog> query, ApplicationUser user)
        {
            if (user.Role == UserRole.systemAdmin) return query;
            return query.Where(log => log.ProjectId.HasValue
                && _context.Project.Any(project => project.Id == log.ProjectId.Value
                    && !project.IsDeleted
                    && (project.LeaderUserId == user.Id
                        || _context.ProjectUsers.Any(member => member.ProjectId == project.Id && member.UserId == user.Id))));
        }

        private string FormatContent(string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return "无数据";
            try
            {
                var jsonDoc = JsonDocument.Parse(content);
                return JsonSerializer.Serialize(jsonDoc.RootElement, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            }
            catch
            {
                return content ?? "无数据";
            }
        }

        private void ValidateDateRange(LogQueryParameters parameters)
        {
            var today = AppTime.Today;

            if (parameters.StartDate.HasValue && parameters.StartDate.Value.Date > today)
                parameters.StartDate = today;

            if (parameters.EndDate.HasValue && parameters.EndDate.Value.Date > today)
                parameters.EndDate = today;

            if (parameters.StartDate.HasValue && parameters.EndDate.HasValue && parameters.EndDate < parameters.StartDate)
                parameters.EndDate = parameters.StartDate;
        }

        public DateTime ConvertToLocalTime(DateTime utcTime)
        {
            return AppTime.ToBeijingTime(utcTime);
        }
    }
}

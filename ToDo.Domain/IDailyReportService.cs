using Microsoft.AspNetCore.Http;
using ToDo.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ToDo.Domain
{
    public interface IDailyReportService
    {
        // 创建相关
        Task<List<DomainSelectListItem>> GetAccessibleProjects(ApplicationUser currentUser, bool activeOnly = false);
        Task<bool> CanAccessProjectAsync(int projectId, ApplicationUser currentUser);
        Task<bool> CanAccessReportAsync(int reportId, ApplicationUser currentUser);
        Task<Project?> GetDailyReportProject(int projectId);
        Task<(bool HasPermission, string ErrorMessage)> CheckReportPermission(ApplicationUser user, Project project);
        Task SaveDailyReport(DailyReport dailyReport, List<IFormFile> attachments, string webRootPath);

        // 详情相关
        Task<DailyReport?> GetDailyReportWithDetails(int id, ApplicationUser currentUser);
        Task<string> GetReporterName(int reporterId);
        Task<List<AttachmentViewModel>> GetAttachmentsByReportId(int reportId);

        // 编辑相关
        Task<DailyReport?> GetOriginalDailyReport(int id);
        Task<(bool CanEdit, string ErrorMessage)> CheckEditPermission(DailyReport original, ApplicationUser currentUser);
        Task DeleteAttachments(List<int> deletedIds, DailyReport original, string webRootPath, ApplicationUser currentUser);
        Task AddNewAttachments(List<IFormFile> newAttachments, DailyReport original, string webRootPath, ApplicationUser currentUser);
        Task UpdateDailyReport(DailyReport original, string title, string content, int reportType, DateTime reportDate, ApplicationUser currentUser);

        // 列表相关
        // 列表相关：新增 filterKeyword 参数
        Task<(List<DailyReport> Items, int TotalCount)> GetFilteredDailyReports(
            int? filterProjectId, int? filterReportType, string filterReporter, string filterKeyword, // 新增关键词参数
            DateTime? filterStartDate, DateTime? filterEndDate,
            int currentPage, int pageSize,
            ApplicationUser currentUser,
            bool excludeTeamReport = false);

        Task<Dictionary<int, bool>> GetCanDeletePermissions(List<DailyReport> items, ApplicationUser currentUser);
        Task<(bool Success, string Message)> DeleteDailyReport(int id, ApplicationUser currentUser);
    }

    // 辅助类
    public class DomainSelectListItem
    {
        public string Text { get; set; }
        public string Value { get; set; }

        public DomainSelectListItem(string text, string value)
        {
            Text = text;
            Value = value;
        }
    }

    public class AttachmentViewModel
    {
        public int Id { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string FileSizeFormatted { get; set; } = string.Empty;
        public DateTime UploadedAt { get; set; }
        public string IconClass { get; set; } = string.Empty;
    }
}

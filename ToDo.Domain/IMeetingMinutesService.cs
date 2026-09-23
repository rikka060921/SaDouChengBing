using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using System.Threading.Tasks;
using ToDo.Domain.Dto;
using ToDo.Entities;

namespace ToDo.Domain
{
    public interface IMeetingMinutesService
    {
        // 创建相关
        Task<List<DomainSelectListItem>> GetAccessibleProjects(ApplicationUser currentUser, bool activeOnly = false);
        Task<(bool HasPermission, string ErrorMessage)> CheckCreatePermission(ApplicationUser user, Project project);
        Task<MeetingBriefing?> GetMeetingBriefingAsync(int projectId, ApplicationUser currentUser);
        Task<bool> CanAccessMeetingAsync(MeetingMinutes meeting, ApplicationUser currentUser);
        Task SaveMeetingMinutes(MeetingMinutes meetingMinutes, List<IFormFile> attachments, string webRootPath, ApplicationUser currentUser);

        // 详情相关
        Task<MeetingMinutes?> GetMeetingMinutesWithDetails(int id);
        Task<string> GetCreatorName(int creatorId);
        Task<List<AttachmentViewModel>> GetAttachmentsByMeetingId(int meetingId);

        // 编辑相关
        Task<MeetingMinutes?> GetOriginalMeetingMinutes(int id);
        Task<bool> CheckEditPermission(MeetingMinutes original, ApplicationUser currentUser, out string errorMsg);
        Task DeleteAttachments(List<int> deletedIds, MeetingMinutes original, string webRootPath, ApplicationUser currentUser);
        Task AddNewAttachments(List<IFormFile> newAttachments, MeetingMinutes original, string webRootPath, ApplicationUser currentUser);
        Task UpdateMeetingMinutes(MeetingMinutes original, string title, string content, ApplicationUser currentUser, bool isDraft = false);

        // 列表相关
        Task<(List<MeetingMinutes> Items, int TotalCount)> GetFilteredMeetingMinutes(
            int? filterProjectId, string filterTitle, string filterCreator,
            string filterKeyword,
            DateTime? filterStartDate, DateTime? filterEndDate,
            int currentPage, int pageSize, ApplicationUser currentUser);
        Task<Dictionary<int, bool>> GetCanEditPermissions(List<MeetingMinutes> items, ApplicationUser currentUser);
        Task<Dictionary<int, bool>> GetCanDeletePermissions(List<MeetingMinutes> items, ApplicationUser currentUser);
        Task<(bool Success, string Message)> DeleteMeetingMinutes(int id, ApplicationUser currentUser);

        // 腾讯会议CLI拉取相关
        Task<List<TencentMeetDailyItem>> QueryTencentDailyMeetListAsync(DateTime meetDate);
        Task<(string MergeContent, string RawJsonJoin, string RecordIds)> BatchMergeTencentMeetTranscriptAsync(
            List<string> recordIds,
            Dictionary<string, string>? titleMap = null);

        // ========== 版本历史相关接口 ==========
        Task<List<MeetingVersion>> GetMeetingVersionListAsync(int meetingMinutesId, ApplicationUser currentUser);
        Task<(string OldContent, string NewContent, string AiDiffSummary)> CompareTwoVersionAsync(int oldVersionId, int newVersionId, ApplicationUser currentUser);
        Task<(bool Success, string Msg)> RollbackToVersionAsync(int meetingMinutesId, int targetVersionId, ApplicationUser currentUser, string webRootPath);
    }
}

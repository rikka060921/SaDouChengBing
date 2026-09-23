using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ToDo.Context;
using ToDo.Domain.AI;
using ToDo.Domain.Dto;
using ToDo.Entities;
using ToDoTaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Domain
{
    public class MeetingMinutesService : IMeetingMinutesService
    {
        private const int EditWindowHours = 72;
        private const long MaxAttachmentBytes = 10 * 1024 * 1024;
        private const int MaxAttachmentCount = 5;
        private static readonly IReadOnlyDictionary<string, string> AllowedAttachmentTypes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".pdf"] = "application/pdf",
                [".doc"] = "application/msword",
                [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                [".xls"] = "application/vnd.ms-excel",
                [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                [".ppt"] = "application/vnd.ms-powerpoint",
                [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                [".jpg"] = "image/jpeg",
                [".jpeg"] = "image/jpeg",
                [".png"] = "image/png",
                [".txt"] = "text/plain"
            };

        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IAIService _aiService;
        private readonly ITencentMeetingLocalCliService _tencentCliService;

        // 构造函数注入
        public MeetingMinutesService(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IAIService aiService,
            ITencentMeetingLocalCliService tencentCliService)
        {
            _context = context;
            _userManager = userManager;
            _aiService = aiService;
            _tencentCliService = tencentCliService;
        }

        #region 历史版本 新增实现
        /// <summary>获取版本列表</summary>
        public async Task<List<MeetingVersion>> GetMeetingVersionListAsync(int meetingMinutesId, ApplicationUser currentUser)
        {
            var meeting = await _context.MeetingMinutes
                .Include(m => m.Project)
                .FirstOrDefaultAsync(x => x.Id == meetingMinutesId && !x.IsDeleted);

            if (meeting == null || !await CanAccessProjectAsync(meeting.Project!, currentUser))
                return new List<MeetingVersion>();

            var versions = await _context.MeetingVersions
                .Include(v => v.Editor)
                .Where(v => v.MeetingMinutesId == meetingMinutesId)
                .OrderByDescending(v => v.VersionNumber)
                .ToListAsync();

            return versions;
        }

        /// <summary>版本内容对比+AI总结差异</summary>
        public async Task<(string OldContent, string NewContent, string AiDiffSummary)> CompareTwoVersionAsync(int oldVersionId, int newVersionId, ApplicationUser currentUser)
        {
            var oldVer = await _context.MeetingVersions.FindAsync(oldVersionId);
            var newVer = await _context.MeetingVersions.FindAsync(newVersionId);

            if (oldVer == null || newVer == null || oldVer.MeetingMinutesId != newVer.MeetingMinutesId)
                return ("", "", "版本数据异常");

            var meeting = await _context.MeetingMinutes
                .Include(m => m.Project)
                .FirstOrDefaultAsync(x => x.Id == oldVer.MeetingMinutesId && !x.IsDeleted);
            if (meeting == null || !await CanAccessProjectAsync(meeting.Project!, currentUser))
                return ("", "", "无权限查看版本差异");

            string oldContent = oldVer.MeetingContent;
            string newContent = newVer.MeetingContent;

            var fallbackSummary = BuildLocalVersionDiffSummary(oldContent, newContent);
            string aiSummary;
            try
            {
                var oldPromptContent = oldContent.Length <= 6000 ? oldContent : oldContent[..6000];
                var newPromptContent = newContent.Length <= 6000 ? newContent : newContent[..6000];
                string prompt = $"对比以下两段会议纪要内容，用一句话精简总结修改了哪些内容，不要多余描述：\n旧内容：{oldPromptContent}\n新内容：{newPromptContent}";
                aiSummary = await _aiService.GetChatCompletionAsync(prompt);
                if (string.IsNullOrWhiteSpace(aiSummary)) aiSummary = fallbackSummary;
            }
            catch
            {
                aiSummary = fallbackSummary;
            }

            return (oldContent, newContent, aiSummary);
        }

        private static string BuildLocalVersionDiffSummary(string oldContent, string newContent)
        {
            if (string.Equals(oldContent, newContent, StringComparison.Ordinal)) return "会议内容没有变化";

            var delta = newContent.Length - oldContent.Length;
            var lengthChange = delta switch
            {
                > 0 => $"增加约 {delta} 个字符",
                < 0 => $"减少约 {-delta} 个字符",
                _ => "总字数基本不变"
            };
            return $"会议内容已调整，{lengthChange}；AI 摘要暂不可用，具体修改请查看下方差异。";
        }

        /// <summary>回滚到指定版本</summary>
        public async Task<(bool Success, string Msg)> RollbackToVersionAsync(int meetingMinutesId, int targetVersionId, ApplicationUser currentUser, string webRootPath)
        {
            using var tran = await _context.Database.BeginTransactionAsync();
            try
            {
                var targetVersion = await _context.MeetingVersions
                    .Include(v => v.Editor)
                    .FirstOrDefaultAsync(v => v.Id == targetVersionId && v.MeetingMinutesId == meetingMinutesId);
                if (targetVersion == null)
                    return (false, "目标版本不存在");

                var meeting = await _context.MeetingMinutes
                    .Include(m => m.Project)
                    .FirstOrDefaultAsync(m => m.Id == meetingMinutesId && !m.IsDeleted);
                if (meeting == null)
                    return (false, "会议纪要不存在");

                if (!await CheckEditPermission(meeting, currentUser, out var permissionError))
                    return (false, permissionError);

                string oldTitle = meeting.MeetingTitle;
                string oldContent = meeting.MeetingContent;

                meeting.MeetingTitle = targetVersion.MeetingTitle;
                meeting.MeetingContent = targetVersion.MeetingContent;
                meeting.IsDraft = targetVersion.IsDraft;
                meeting.LastModifiedAt = AppTime.Now;
                _context.Update(meeting);

                int maxVerNum = await _context.MeetingVersions
                    .Where(v => v.MeetingMinutesId == meetingMinutesId)
                    .MaxAsync(v => (int?)v.VersionNumber) ?? 0;
                int newVersionNo = maxVerNum + 1;

                _context.MeetingVersions.Add(new MeetingVersion
                {
                    MeetingMinutesId = meetingMinutesId,
                    VersionNumber = newVersionNo,
                    MeetingTitle = targetVersion.MeetingTitle,
                    MeetingContent = targetVersion.MeetingContent,
                    IsDraft = targetVersion.IsDraft,
                    EditorId = currentUser.Id
                });

                await _context.SaveChangesAsync();

                var creatorName = await GetCreatorName(meeting.CreatorId);
                var rollbackLog = new ChangeLog
                {
                    OperationType = OperationType.更新,
                    OperationStatus = OperationStatus.成功,
                    OperatedAt = DateTime.UtcNow,
                    OperationTarget = OperationTarget.会议纪要,
                    OperatedByUserId = currentUser.Id,
                    OperatedByUserName = currentUser.UserName,
                    OperatedByRealName = currentUser.RealName,
                    ProjectId = meeting.ProjectId,
                    ProjectName = meeting.Project?.Name,
                    TargetId = meeting.Id,
                    TargetName = meeting.MeetingTitle,
                    BeforeContent = $"回滚前版本：V{maxVerNum}\n原标题：{oldTitle}",
                    AfterContent = $"已回滚至版本V{targetVersion.VersionNumber}，生成新版本V{newVersionNo}"
                };
                _context.ChangeLogs.Add(rollbackLog);

                await _context.SaveChangesAsync();
                await tran.CommitAsync();

                return (true, $"已成功回滚至版本V{targetVersion.VersionNumber}，系统自动生成新版本V{newVersionNo}");
            }
            catch (Exception ex)
            {
                await tran.RollbackAsync();
                return (false, $"回滚失败：{ex.Message}");
            }
        }
        #endregion

        #region 创建相关
        public async Task<List<DomainSelectListItem>> GetAccessibleProjects(ApplicationUser currentUser, bool activeOnly = false)
        {
            if (currentUser == null) return new List<DomainSelectListItem>();

            bool isSystemAdmin = currentUser.Role == UserRole.systemAdmin;
            if (isSystemAdmin)
            {
                return await _context.Project
                    .Where(p => !p.IsDeleted && (!activeOnly || p.Status == ProjectStatus.Active))
                    .OrderBy(p => p.Name)
                    .Select(p => new DomainSelectListItem(p.Name + (p.Status == ProjectStatus.Archived ? "（已归档）" : ""), p.Id.ToString()))
                    .ToListAsync();
            }

            return await _context.Project
                .Where(p => !p.IsDeleted && (!activeOnly || p.Status == ProjectStatus.Active)
                            && (p.LeaderUserId == currentUser.Id
                                || _context.ProjectUsers.Any(pu => pu.ProjectId == p.Id
                                    && pu.UserId == currentUser.Id
                                    && (p.IsEncrypted != '2'
                                        || pu.ProjectRole == (int)ProjectRole.Admin)))
                )
                .OrderBy(p => p.Name)
                .Select(p => new DomainSelectListItem(p.Name + (p.Status == ProjectStatus.Archived ? "（已归档）" : ""), p.Id.ToString()))
                .ToListAsync();
        }

        public async Task<(bool HasPermission, string ErrorMessage)> CheckCreatePermission(ApplicationUser user, Project project)
        {
            if (project.IsDeleted || project.Status != ProjectStatus.Active) return (false, ProjectLifecycleRules.ReadOnlyMessage);
            bool isSystemAdmin = user.Role == UserRole.systemAdmin;
            bool isProjectAdmin = await _context.ProjectUsers
                .AnyAsync(pu => pu.ProjectId == project.Id && pu.UserId == user.Id && pu.ProjectRole == (int)ProjectRole.Admin);

            if (isSystemAdmin || isProjectAdmin)
                return (true, string.Empty);

            bool isProjectMember = await _context.ProjectUsers
                .AnyAsync(pu => pu.ProjectId == project.Id && pu.UserId == user.Id);
            if (!isProjectMember)
                return (false, $"你不是「{project.Name}」的项目成员，无法创建会议纪要");

            if (project.IsEncrypted == '2' && project.LeaderUserId != user.Id)
                return (false, $"「{project.Name}」为私密项目，仅负责人可创建会议纪要");

            return (true, string.Empty);
        }

        public async Task<MeetingBriefing?> GetMeetingBriefingAsync(int projectId, ApplicationUser currentUser)
        {
            var project = await _context.Project
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == projectId && !item.IsDeleted);
            if (project == null || !await CanAccessProjectAsync(project, currentUser))
                return null;

            // 获取上一场会议（已提交的非草稿）
            var previousMeeting = await _context.MeetingMinutes
                .AsNoTracking()
                .Include(item => item.ActionItems)
                    .ThenInclude(item => item.Assignee)
                .Include(item => item.ActionItems)
                    .ThenInclude(item => item.MatchedTask)
                        .ThenInclude(item => item!.Assignee)
                .Where(item => item.ProjectId == projectId
                               && !item.IsDeleted
                               && !item.IsDraft)
                .OrderByDescending(item => item.MeetingDate)
                .ThenByDescending(item => item.CreatedAt)
                .FirstOrDefaultAsync();

            // 判断是否有已确认写入的任务
            var hasConfirmedTasks = previousMeeting != null
                && previousMeeting.ActionItems.Any(ai => ai.SyncStatus.StartsWith("已"));

            // 获取上一场会议关联的任务ID列表
            var previousMeetingTaskIds = previousMeeting?.ActionItems
                .Where(ai => ai.MatchedTaskId.HasValue)
                .Select(ai => ai.MatchedTaskId.GetValueOrDefault())
                .ToHashSet() ?? new HashSet<int>();

            // ===== 1. 获取"上次会议创建的任务中已完成的任务" =====
            List<MeetingBriefingCompletedTask> completedTasks = new();
            if (previousMeetingTaskIds.Any())
            {
                var completedTaskEntities = await _context.ToDoTasks
                    .AsNoTracking()
                    .Include(item => item.Assignee)
                    .Where(item => previousMeetingTaskIds.Contains(item.Id)
                                   && !item.IsDeleted
                                   && (item.IsCompleted || item.Status == ToDoTaskStatus.Completed))
                    .OrderByDescending(item => item.EndTime)
                    .ToListAsync();

                completedTasks = completedTaskEntities.Select(item => new MeetingBriefingCompletedTask
                {
                    Id = item.Id,
                    Title = item.Title,
                    Assignee = item.AssigneeType == TaskAssigneeType.DigitalEmployee
          ? item.AgentName ?? "未指定 Agent"
          : item.Assignee?.RealName ?? item.Assignee?.UserName ?? "未分配",
                    AssigneeType = item.AssigneeType == TaskAssigneeType.DigitalEmployee ? "Agent" : "人员",
                    CompletedAt = item.UpdatedAt,
                    CompletionDate = item.UpdatedAt.ToString("yyyy-MM-dd")    // ← 改成用 UpdatedAt
                }).ToList();
            }

            // ===== 2. 获取"当前未完成项目任务"（排除上一场会议关联的任务） =====
            var openTasksExcludingPrevious = await _context.ToDoTasks
                .AsNoTracking()
                .Include(item => item.Assignee)
                .Include(item => item.ParentTask)
                .Where(item => item.ProjectId == projectId
                               && !item.IsDeleted
                               && !item.IsCompleted
                               && item.Status != ToDoTaskStatus.Completed
                               && item.Status != ToDoTaskStatus.Cancelled
                               && !previousMeetingTaskIds.Contains(item.Id))
                .OrderBy(item => item.EndTime == null)
                .ThenBy(item => item.EndTime)
                .ThenByDescending(item => item.Priority)
                .ThenBy(item => item.Title)
                .ToListAsync();

            // ===== 2b. 获取"未完成任务"（排除已完成和已取消的，用于统计和展示） =====
            var allOpenTasks = await _context.ToDoTasks
                .AsNoTracking()
                .Include(item => item.Assignee)
                .Include(item => item.ParentTask)
                .Where(item => item.ProjectId == projectId
                               && !item.IsDeleted
                               && !item.IsCompleted
                               && item.Status != ToDoTaskStatus.Completed
                               && item.Status != ToDoTaskStatus.Cancelled)
                .OrderBy(item => item.EndTime == null)
                .ThenBy(item => item.EndTime)
                .ThenByDescending(item => item.Priority)
                .ThenBy(item => item.Title)
                .ToListAsync();

            return new MeetingBriefing
            {
                ProjectId = project.Id,
                ProjectName = project.Name,
                PreviousMeeting = previousMeeting == null ? null : new MeetingBriefingPreviousMeeting
                {
                    Id = previousMeeting.Id,
                    Title = previousMeeting.MeetingTitle,
                    MeetingDate = previousMeeting.MeetingDate.ToString("yyyy-MM-dd"),
                    Summary = TrimForBriefing(previousMeeting.AiSummary ?? previousMeeting.MeetingContent),
                    HasConfirmedTasks = hasConfirmedTasks,
                    IsConfirmed = previousMeeting.ConfirmedAt.HasValue,
                    ActionItems = previousMeeting.ActionItems
               .Where(item => item.SyncStatus.StartsWith("已"))
               .OrderByDescending(item => item.CreatedAt)
               .Select(item => new MeetingBriefingActionItem
               {
                   Content = item.Content,
                   // 优先取【真实任务】最新的责任人，没有关联任务再退回快照
                   Assignee = item.MatchedTask != null
                       ? (item.MatchedTask.AssigneeType == TaskAssigneeType.DigitalEmployee
                           ? item.MatchedTask.AgentName ?? "未指定 Agent"
                           : item.MatchedTask.Assignee?.RealName ?? item.MatchedTask.Assignee?.UserName ?? "未分配")
                       : (item.Assignee?.RealName ?? item.Assignee?.UserName ?? item.AssigneeText ?? "未识别"),

                   // 优先取【真实任务】最新截止时间，无关联任务退回快照
                   Deadline = item.MatchedTask != null && item.MatchedTask.EndTime.HasValue
                       ? item.MatchedTask.EndTime.Value.ToString("yyyy-MM-dd")
                       : (item.Deadline?.ToString("yyyy-MM-dd") ?? "未设置"),

                   TaskStatus = item.MatchedTask == null ? "未关联任务" : GetTaskStatusText(item.MatchedTask),
                   TaskId = item.MatchedTaskId
               })
               .ToList()
                },
                CompletedTasksSinceLastMeeting = completedTasks,
                OpenTasks = allOpenTasks.Select(item => new MeetingBriefingTask
                {
                    Id = item.Id,
                    Title = item.Title,
                    Assignee = item.AssigneeType == TaskAssigneeType.DigitalEmployee
                        ? item.AgentName ?? "未指定 Agent"
                        : item.Assignee?.RealName ?? item.Assignee?.UserName ?? "未分配",
                    AssigneeType = item.AssigneeType == TaskAssigneeType.DigitalEmployee ? "Agent" : "人员",
                    Status = GetTaskStatusText(item),
                    Priority = GetTaskPriorityText(item.Priority),
                    Deadline = item.EndTime?.ToString("yyyy-MM-dd") ?? "未设置",
                    DeadlineAt = item.EndTime,
                    IsSubTask = item.ParentTaskId.HasValue,
                    ParentTaskTitle = item.ParentTask?.Title
                }).ToList(),
                OpenTasksExcludingPreviousMeeting = openTasksExcludingPrevious.Select(item => new MeetingBriefingTask
                {
                    Id = item.Id,
                    Title = item.Title,
                    Assignee = item.AssigneeType == TaskAssigneeType.DigitalEmployee
                        ? item.AgentName ?? "未指定 Agent"
                        : item.Assignee?.RealName ?? item.Assignee?.UserName ?? "未分配",
                    AssigneeType = item.AssigneeType == TaskAssigneeType.DigitalEmployee ? "Agent" : "人员",
                    Status = GetTaskStatusText(item),
                    Priority = GetTaskPriorityText(item.Priority),
                    Deadline = item.EndTime?.ToString("yyyy-MM-dd") ?? "未设置",
                    DeadlineAt = item.EndTime,
                    IsSubTask = item.ParentTaskId.HasValue,
                    ParentTaskTitle = item.ParentTask?.Title
                }).ToList()
            };
        }


        public async Task<bool> CanAccessMeetingAsync(MeetingMinutes meeting, ApplicationUser currentUser)
        {
            if (currentUser == null) return false;
            if (currentUser.Role == UserRole.systemAdmin) return true;

            // ===== 多项目改造：取所有关联项目 =====
            var projectIds = await _context.MeetingMinutesProjects
                .Where(mp => mp.MeetingMinutesId == meeting.Id)
                .Select(mp => mp.ProjectId)
                .ToListAsync();

            // 兜底：历史数据没写关联表，用首项目
            if (projectIds.Count == 0) projectIds.Add(meeting.ProjectId);

            // 对任意一个项目有权限即可访问
            foreach (var pid in projectIds)
            {
                var project = await _context.Project.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == pid && !p.IsDeleted);
                if (project != null && await CanAccessProjectAsync(project, currentUser))
                    return true;
            }
            return false;
        }

        private async Task<bool> CanAccessProjectAsync(Project project, ApplicationUser currentUser)
        {
            if (currentUser == null || project.IsDeleted) return false;
            if (currentUser.Role == UserRole.systemAdmin) return true;
            if (project.LeaderUserId == currentUser.Id) return true;

            var membership = await _context.ProjectUsers
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.ProjectId == project.Id && item.UserId == currentUser.Id);
            return membership != null
                && (project.IsEncrypted != '2' || membership.ProjectRole == (int)ProjectRole.Admin);
        }

        private static string TrimForBriefing(string? content)
        {
            var value = string.IsNullOrWhiteSpace(content) ? "暂无会议摘要" : content.Trim();
            return value.Length <= 1000 ? value : value[..1000] + "...";
        }

        private static string GetTaskStatusText(ToDoTask task)
        {
            if (task.IsCompleted || task.Status == ToDoTaskStatus.Completed) return "已完成";
            return task.Status switch
            {
                ToDoTaskStatus.InProgress => "进行中",
                ToDoTaskStatus.PendingConfirmation => "待审核",
                ToDoTaskStatus.Cancelled => "已取消",
                _ => "未开始"
            };
        }

        private static string GetTaskPriorityText(TaskPriority priority) => priority switch
        {
            TaskPriority.High => "高",
            TaskPriority.Low => "低",
            _ => "中"
        };

        public async Task SaveMeetingMinutes(MeetingMinutes meetingMinutes, List<IFormFile> attachments, string webRootPath, ApplicationUser currentUser)
        {
            foreach (var projectId in meetingMinutes.MeetingProjects.Select(p => p.ProjectId).Append(meetingMinutes.ProjectId).Distinct())
                await ProjectLifecycleRules.RequireActiveAsync(_context, projectId);
            ValidateAttachments(attachments);
            var writtenFiles = new List<string>();
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                if (string.IsNullOrWhiteSpace(meetingMinutes.MeetingTitle))
                {
                    throw new ArgumentException("会议标题不能为空，且不能为空白（包括全空格）");
                }

                _context.MeetingMinutes.Add(meetingMinutes);
                await _context.SaveChangesAsync();

                _context.MeetingVersions.Add(new MeetingVersion
                {
                    MeetingMinutesId = meetingMinutes.Id,
                    VersionNumber = 1,
                    MeetingTitle = meetingMinutes.MeetingTitle,
                    MeetingContent = meetingMinutes.MeetingContent ?? string.Empty,
                    IsDraft = meetingMinutes.IsDraft,
                    EditorId = currentUser.Id
                });
                await _context.SaveChangesAsync();

                List<string> addedAttachmentNames = new();
                if (attachments != null && attachments.Any())
                {
                    var uploadDir = Path.Combine(
                        webRootPath,
                        "uploads",
                        "meeting",
                        meetingMinutes.ProjectId.ToString(),
                        AppTime.Today.ToString("yyyyMMdd")
                    );
                    Directory.CreateDirectory(uploadDir);

                    var meetingAttachments = new List<MeetingAttachment>();
                    foreach (var file in attachments)
                    {
                        var (originalName, ext, contentType) = ValidateAttachment(file);
                        var fileName = $"{Guid.NewGuid()}{ext}";
                        var filePath = Path.Combine(uploadDir, fileName);

                        using (var stream = new FileStream(filePath, FileMode.Create))
                        {
                            await file.CopyToAsync(stream);
                        }
                        writtenFiles.Add(filePath);

                        var attachment = new MeetingAttachment
                        {
                            FileName = originalName,
                            FilePath = Path.Combine("uploads", "meeting", meetingMinutes.ProjectId.ToString(), AppTime.Today.ToString("yyyyMMdd"), fileName),
                            ContentType = contentType,
                            FileSize = file.Length,
                            UploadedAt = AppTime.Now,
                            MeetingMinutesId = meetingMinutes.Id
                        };
                        meetingAttachments.Add(attachment);
                        addedAttachmentNames.Add(originalName);
                    }

                    _context.MeetingAttachments.AddRange(meetingAttachments);
                    await _context.SaveChangesAsync();
                }

                var creatorName = await GetCreatorName(meetingMinutes.CreatorId);

                var createLog = new ChangeLog
                {
                    OperationType = OperationType.创建,
                    OperationStatus = OperationStatus.成功,
                    OperatedAt = DateTime.UtcNow,
                    OperationTarget = OperationTarget.会议纪要,
                    OperatedByUserId = currentUser.Id,
                    OperatedByUserName = currentUser.UserName,
                    OperatedByRealName = currentUser.RealName,
                    ProjectId = meetingMinutes.ProjectId,
                    ProjectName = meetingMinutes.Project?.Name,
                    TargetId = meetingMinutes.Id,
                    TargetName = meetingMinutes.MeetingTitle,
                    AfterContent = $"创建人：{creatorName}\n" +
                                  $"标题：{meetingMinutes.MeetingTitle}\n" +
                                  $"报告日期：{meetingMinutes.MeetingDate:yyyy-MM-dd}\n" +
                                  $"创建时间：{meetingMinutes.CreatedAt:yyyy-MM-dd HH:mm}\n" +
                                  (addedAttachmentNames.Any() ? $"附件：{string.Join("、", addedAttachmentNames)}" : "")
                };
                _context.ChangeLogs.Add(createLog);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                DeleteWrittenFiles(writtenFiles);
                throw;
            }
        }
        #endregion

        #region 详情相关
        public async Task<MeetingMinutes?> GetMeetingMinutesWithDetails(int id)
        {
            return await _context.MeetingMinutes
                .Include(mm => mm.Project)
                .Include(mm => mm.MeetingProjects)          // ===== 新增 =====
                    .ThenInclude(mp => mp.Project)          // ===== 新增 =====
                .Include(mm => mm.Attachments.Where(a => !a.IsDeleted))
                .Include(mm => mm.Versions.OrderByDescending(v => v.VersionNumber))
                    .ThenInclude(v => v.Editor)
                .Include(mm => mm.ActionItems)
                    .ThenInclude(item => item.Assignee)
                .Include(mm => mm.ActionItems)
                    .ThenInclude(item => item.MatchedTask)
                .FirstOrDefaultAsync(mm => mm.Id == id && !mm.IsDeleted);
        }

        public async Task<string> GetCreatorName(int creatorId)
        {
            var creator = await _context.Users.FindAsync(creatorId);
            return creator?.RealName ?? creator?.UserName ?? "未知用户";
        }

        public async Task<List<AttachmentViewModel>> GetAttachmentsByMeetingId(int meetingId)
        {
            return await _context.MeetingAttachments
                .AsNoTracking()
                .Where(a => a.MeetingMinutesId == meetingId && !a.IsDeleted)
                .Select(a => new AttachmentViewModel
                {
                    Id = a.Id,
                    FileName = a.FileName,
                    FilePath = a.FilePath,
                    FileSize = a.FileSize,
                    FileSizeFormatted = FormatFileSize(a.FileSize),
                    UploadedAt = a.UploadedAt,
                    IconClass = GetFileIconClass(a.FileName)
                })
                .ToListAsync();
        }
        #endregion

        #region 编辑相关
        public async Task<MeetingMinutes?> GetOriginalMeetingMinutes(int id)
        {
            return await _context.MeetingMinutes
                .Include(m => m.Attachments.Where(a => !a.IsDeleted))
                .Include(m => m.Project)
                .Include(m => m.Versions.OrderByDescending(v => v.VersionNumber))
                .FirstOrDefaultAsync(m => m.Id == id && !m.IsDeleted);
        }

        public async Task<List<AttachmentViewModel>> GetActiveAttachmentsForEdit(int meetingId)
        {
            return await _context.MeetingAttachments
                .AsNoTracking()
                .Where(a => a.MeetingMinutesId == meetingId && !a.IsDeleted)
                .Select(a => new AttachmentViewModel
                {
                    Id = a.Id,
                    FileName = a.FileName,
                    FilePath = a.FilePath,
                    FileSize = a.FileSize,
                    FileSizeFormatted = FormatFileSize(a.FileSize),
                    UploadedAt = a.UploadedAt,
                    IconClass = GetFileIconClass(a.FileName)
                })
                .ToListAsync();
        }

        public Task<bool> CheckEditPermission(MeetingMinutes original, ApplicationUser currentUser, out string errorMsg)
        {
            errorMsg = string.Empty;
            if (original == null || currentUser == null)
            {
                errorMsg = "会议纪要或当前用户不存在";
                return Task.FromResult(false);
            }

            if (_context.Project.AsNoTracking().Any(p => p.Id == original.ProjectId && (p.IsDeleted || p.Status == ProjectStatus.Archived))
                || _context.MeetingMinutesProjects.Any(link => link.MeetingMinutesId == original.Id
                    && _context.Project.Any(p => p.Id == link.ProjectId && (p.IsDeleted || p.Status == ProjectStatus.Archived))))
            {
                errorMsg = ProjectLifecycleRules.ReadOnlyMessage;
                return Task.FromResult(false);
            }
            if (AppTime.Now > original.CreatedAt.AddHours(EditWindowHours))
            {
                errorMsg = $"会议纪要创建已超过{EditWindowHours}小时，任何人都无法再编辑";
                return Task.FromResult(false);
            }

            bool isCreator = original.CreatorId == currentUser.Id;
            bool isSystemAdmin = currentUser.Role == UserRole.systemAdmin;
            bool isProjectLeader = original.Project?.LeaderUserId == currentUser.Id
                || _context.Project.AsNoTracking()
                    .Any(item => item.Id == original.ProjectId && item.LeaderUserId == currentUser.Id);
            bool isProjectAdmin = _context.ProjectUsers
                .AsNoTracking()
                .Any(item => item.ProjectId == original.ProjectId
                    && item.UserId == currentUser.Id
                    && item.ProjectRole == (int)ProjectRole.Admin);

            if (!(isCreator || isProjectLeader || isProjectAdmin || isSystemAdmin))
            {
                errorMsg = $"仅会议纪要创建者、项目负责人、项目管理员或系统管理员可在创建后{EditWindowHours}小时内编辑";
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public async Task DeleteAttachments(List<int> deletedIds, MeetingMinutes original, string webRootPath, ApplicationUser currentUser)
        {
            if (!deletedIds.Any() || original == null) return;
            if (!await CheckEditPermission(original, currentUser, out var permissionError))
                throw new UnauthorizedAccessException(permissionError);

            var physicalFilesToDelete = new List<string>();
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var attachmentsToDelete = await _context.MeetingAttachments
                    .Where(att => deletedIds.Contains(att.Id)
                                && att.MeetingMinutesId == original.Id
                                && !att.IsDeleted)
                    .ToListAsync();

                if (!attachmentsToDelete.Any()) return;

                List<string> deletedFileNames = attachmentsToDelete.Select(att => att.FileName).ToList();
                var creatorName = await GetCreatorName(original.CreatorId);

                foreach (var att in attachmentsToDelete)
                {
                    if (!string.IsNullOrEmpty(att.FilePath))
                    {
                        var meetingRoot = Path.GetFullPath(Path.Combine(webRootPath, "uploads", "meeting"));
                        var fullPath = Path.GetFullPath(Path.Combine(webRootPath, att.FilePath));
                        if (fullPath.StartsWith(meetingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(fullPath))
                        {
                            physicalFilesToDelete.Add(fullPath);
                        }
                    }

                    att.IsDeleted = true;
                    att.LastModifiedAt = AppTime.Now;
                    _context.MeetingAttachments.Update(att);
                }

                await _context.SaveChangesAsync();

                var deleteLog = new ChangeLog
                {
                    OperationType = OperationType.删除,
                    OperationStatus = OperationStatus.成功,
                    OperatedAt = DateTime.UtcNow,
                    OperationTarget = OperationTarget.会议纪要,
                    OperatedByUserId = currentUser.Id,
                    OperatedByUserName = currentUser.UserName,
                    OperatedByRealName = currentUser.RealName,
                    ProjectId = original.ProjectId,
                    ProjectName = original.Project?.Name,
                    TargetId = original.Id,
                    TargetName = original.MeetingTitle,
                    BeforeContent = $"删除附件：{string.Join("、", deletedFileNames)}\n创建人：{creatorName}"
                };
                _context.ChangeLogs.Add(deleteLog);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();
                DeleteWrittenFiles(physicalFilesToDelete);
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task AddNewAttachments(List<IFormFile> newAttachments, MeetingMinutes original, string webRootPath, ApplicationUser currentUser)
        {
            if (newAttachments == null || !newAttachments.Any()) return;
            if (!await CheckEditPermission(original, currentUser, out var permissionError))
                throw new UnauthorizedAccessException(permissionError);

            ValidateAttachments(newAttachments);
            var activeAttachmentCount = await _context.MeetingAttachments
                .CountAsync(attachment => attachment.MeetingMinutesId == original.Id && !attachment.IsDeleted);
            if (activeAttachmentCount + newAttachments.Count > MaxAttachmentCount)
                throw new InvalidOperationException($"附件总数不能超过 {MaxAttachmentCount} 个");

            var writtenFiles = new List<string>();
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var projectId = original.ProjectId.ToString();
                var dateDir = AppTime.Today.ToString("yyyyMMdd");
                var uploadDir = Path.Combine(webRootPath ?? "", "uploads", "meeting", projectId, dateDir);
                Directory.CreateDirectory(uploadDir);

                List<string> addedFileNames = new();

                foreach (var file in newAttachments)
                {
                    var (originalName, ext, contentType) = ValidateAttachment(file);
                    var fileName = $"{Guid.NewGuid()}{ext}";
                    var filePath = Path.Combine(uploadDir, fileName);

                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }
                    writtenFiles.Add(filePath);

                    var attachment = new MeetingAttachment
                    {
                        FileName = originalName,
                        FilePath = Path.Combine("uploads", "meeting", projectId, dateDir, fileName).Replace("\\", "/"),
                        ContentType = contentType,
                        FileSize = file.Length,
                        UploadedAt = AppTime.Now,
                        MeetingMinutesId = original.Id
                    };
                    original.Attachments.Add(attachment);
                    addedFileNames.Add(originalName);
                }

                await _context.SaveChangesAsync();

                var creatorName = await GetCreatorName(original.CreatorId);

                var attachmentLog = new ChangeLog
                {
                    OperationType = OperationType.更新,
                    OperationStatus = OperationStatus.成功,
                    OperatedAt = DateTime.UtcNow,
                    OperationTarget = OperationTarget.会议纪要,
                    OperatedByUserId = currentUser.Id,
                    OperatedByUserName = currentUser.UserName,
                    OperatedByRealName = currentUser.RealName,
                    ProjectId = original.ProjectId,
                    ProjectName = original.Project?.Name,
                    TargetId = original.Id,
                    TargetName = original.MeetingTitle,
                    AfterContent = $"新增附件：{string.Join("、", addedFileNames)}\n" +
                                 $"创建人：{creatorName}"
                };
                _context.ChangeLogs.Add(attachmentLog);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                DeleteWrittenFiles(writtenFiles);
                throw;
            }
        }

        private static void ValidateAttachments(IReadOnlyCollection<IFormFile>? files)
        {
            if (files == null || files.Count == 0) return;
            if (files.Count > MaxAttachmentCount)
                throw new InvalidOperationException($"最多上传 {MaxAttachmentCount} 个附件");
            foreach (var file in files) ValidateAttachment(file);
        }

        private static (string OriginalName, string Extension, string ContentType) ValidateAttachment(IFormFile file)
        {
            if (file == null || file.Length <= 0)
                throw new InvalidOperationException("附件不能为空");
            if (file.Length > MaxAttachmentBytes)
                throw new InvalidOperationException("单个附件不能超过 10 MB");

            var originalName = Path.GetFileName(file.FileName ?? string.Empty);
            var extension = Path.GetExtension(originalName).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(originalName) || !AllowedAttachmentTypes.TryGetValue(extension, out var contentType))
                throw new InvalidOperationException("附件格式不支持");
            return (originalName, extension, contentType);
        }

        private static void DeleteWrittenFiles(IEnumerable<string> files)
        {
            foreach (var file in files)
            {
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                }
                catch
                {
                    // Best-effort cleanup; preserve the original operation result.
                }
            }
        }

        public async Task UpdateMeetingMinutes(MeetingMinutes original, string title, string content, ApplicationUser currentUser, bool isDraft = false)
        {
            if (!await CheckEditPermission(original, currentUser, out var permissionError))
                throw new UnauthorizedAccessException(permissionError);

            bool hasTitleChange = original.MeetingTitle != title?.Trim();
            bool hasContentChange = original.MeetingContent != content?.Trim();
            bool hasDraftChange = original.IsDraft != isDraft;
            if (!hasTitleChange && !hasContentChange && !hasDraftChange)
                return;

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                string oldTitle = original.MeetingTitle;

                original.MeetingTitle = title?.Trim() ?? "";
                original.MeetingContent = content?.Trim() ?? "";
                original.IsDraft = isDraft;
                original.SubmittedAt = isDraft ? null : (original.SubmittedAt ?? AppTime.Now);
                original.LastModifiedAt = AppTime.Now;
                _context.Update(original);
                await _context.SaveChangesAsync();

                var nextVersion = (await _context.MeetingVersions
                    .Where(version => version.MeetingMinutesId == original.Id)
                    .Select(version => (int?)version.VersionNumber)
                    .MaxAsync() ?? 0) + 1;
                _context.MeetingVersions.Add(new MeetingVersion
                {
                    MeetingMinutesId = original.Id,
                    VersionNumber = nextVersion,
                    MeetingTitle = original.MeetingTitle,
                    MeetingContent = original.MeetingContent,
                    IsDraft = original.IsDraft,
                    EditorId = currentUser.Id
                });
                await _context.SaveChangesAsync();

                var creatorName = await GetCreatorName(original.CreatorId);

                List<string> changes = new();
                if (hasTitleChange)
                {
                    changes.Add($"标题：{oldTitle} → {original.MeetingTitle}");
                }
                if (hasContentChange)
                {
                    changes.Add("内容：已修改");
                }

                var updateLog = new ChangeLog
                {
                    OperationType = OperationType.更新,
                    OperationStatus = OperationStatus.成功,
                    OperatedAt = DateTime.UtcNow,
                    OperationTarget = OperationTarget.会议纪要,
                    OperatedByUserId = currentUser.Id,
                    OperatedByUserName = currentUser.UserName,
                    OperatedByRealName = currentUser.RealName,
                    ProjectId = original.ProjectId,
                    ProjectName = original.Project?.Name,
                    TargetId = original.Id,
                    TargetName = original.MeetingTitle,
                    BeforeContent = $"{string.Join("\n", changes.Select(c =>
                        c.StartsWith("标题") ? $"{c.Split("→")[0].Trim()}" : "内容：（原内容）"))}\n" +
                        $"创建人：{creatorName}\n",
                    AfterContent = $"{string.Join("\n", changes.Select(c =>
                        c.StartsWith("标题") ? $"{c.Split("→")[1].Trim()}" : "内容：（已修改）"))}\n" +
                        $"创建人：{creatorName}\n"
                };
                _context.ChangeLogs.Add(updateLog);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        #endregion

        #region 列表相关
        public async Task<(List<MeetingMinutes> Items, int TotalCount)> GetFilteredMeetingMinutes(
            int? filterProjectId, string filterTitle, string filterCreator, string filterKeyword,
            DateTime? filterStartDate, DateTime? filterEndDate,
            int currentPage, int pageSize, ApplicationUser currentUser)
        {
            var query = _context.MeetingMinutes
                .Include(mm => mm.Project)
                .Where(mm => !mm.IsDeleted)
                .AsQueryable();

            if (currentUser.Role != UserRole.systemAdmin)
            {
                // ===== 多项目改造：对任意一个关联项目有权限即可见 =====
                query = query.Where(mm =>
                    // 首项目有权限
                    (mm.Project != null
                        && (mm.Project.LeaderUserId == currentUser.Id
                            || _context.ProjectUsers.Any(member =>
                                member.ProjectId == mm.ProjectId
                                && member.UserId == currentUser.Id
                                && (mm.Project.IsEncrypted != '2'
                                    || member.ProjectRole == (int)ProjectRole.Admin))))
                    // 或任意一个关联项目有权限
                    || _context.MeetingMinutesProjects.Any(mp =>
                        mp.MeetingMinutesId == mm.Id
                        && _context.Project.Any(p => p.Id == mp.ProjectId && !p.IsDeleted
                            && (p.LeaderUserId == currentUser.Id
                                || _context.ProjectUsers.Any(pu =>
                                    pu.ProjectId == p.Id
                                    && pu.UserId == currentUser.Id
                                    && (p.IsEncrypted != '2'
                                        || pu.ProjectRole == (int)ProjectRole.Admin))))));
            }

            if (filterProjectId.HasValue)
            {
                // ===== 多项目改造：关联了该项目就显示（不管是不是首项目）=====
                query = query.Where(mm =>
                    mm.ProjectId == filterProjectId.Value
                    || _context.MeetingMinutesProjects.Any(mp =>
                        mp.MeetingMinutesId == mm.Id && mp.ProjectId == filterProjectId.Value));
            }

            if (!string.IsNullOrEmpty(filterTitle))
                query = query.Where(mm => mm.MeetingTitle.Contains(filterTitle));
            if (!string.IsNullOrEmpty(filterCreator))
            {
                var matchingUserIds = await _context.Users
                    .Where(u => (u.UserName != null && u.UserName.Contains(filterCreator))
                        || (u.RealName != null && u.RealName.Contains(filterCreator)))
                    .Select(u => u.Id)
                    .ToListAsync();
                query = query.Where(mm => matchingUserIds.Contains(mm.CreatorId));
            }
            if (!string.IsNullOrEmpty(filterKeyword))
                query = query.Where(mm => mm.MeetingContent.Contains(filterKeyword));
            if (filterStartDate.HasValue)
                query = query.Where(mm => mm.MeetingDate >= filterStartDate.Value);
            if (filterEndDate.HasValue)
                query = query.Where(mm => mm.MeetingDate <= filterEndDate.Value.AddDays(1).AddTicks(-1));

            var totalCount = await query.CountAsync();
            var items = await query
                .OrderByDescending(mm => mm.MeetingDate)
                .Skip((currentPage - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (items, totalCount);
        }

        public async Task<Dictionary<int, bool>> GetCanEditPermissions(List<MeetingMinutes> items, ApplicationUser currentUser)
        {
            var result = new Dictionary<int, bool>();
            var projectIds = items.Select(item => item.ProjectId).Distinct().ToList();
            var administeredProjectIds = (await _context.ProjectUsers
                    .AsNoTracking()
                    .Where(item => item.UserId == currentUser.Id
                        && item.ProjectRole == (int)ProjectRole.Admin
                        && projectIds.Contains(item.ProjectId))
                    .Select(item => item.ProjectId)
                    .ToListAsync())
                .ToHashSet();
            var ledProjectIds = (await _context.Project
                    .AsNoTracking()
                    .Where(item => item.LeaderUserId == currentUser.Id && projectIds.Contains(item.Id))
                    .Select(item => item.Id)
                    .ToListAsync())
                .ToHashSet();
            var now = AppTime.Now;

            foreach (var item in items)
            {
                bool withinEditWindow = now <= item.CreatedAt.AddHours(EditWindowHours);
                bool isCreator = item.CreatorId == currentUser.Id;
                bool hasRolePermission = isCreator
                    || currentUser.Role == UserRole.systemAdmin
                    || ledProjectIds.Contains(item.ProjectId)
                    || administeredProjectIds.Contains(item.ProjectId);
                result[item.Id] = withinEditWindow && hasRolePermission;
            }
            return result;
        }

        public async Task<Dictionary<int, bool>> GetCanDeletePermissions(List<MeetingMinutes> items, ApplicationUser currentUser)
        {
            var result = new Dictionary<int, bool>();
            foreach (var item in items)
            {
                bool within48Hours = (AppTime.Now - item.CreatedAt) <= TimeSpan.FromHours(48);
                bool isCreator = item.CreatorId == currentUser.Id;
                bool isProjectAdmin = await _context.ProjectUsers
                    .AnyAsync(pu => pu.ProjectId == item.ProjectId && pu.UserId == currentUser.Id && pu.ProjectRole == (int)ProjectRole.Admin);
                bool isSystemAdmin = currentUser.Role == UserRole.systemAdmin;

                result[item.Id] = within48Hours && (isCreator || isProjectAdmin || isSystemAdmin);
            }
            return result;
        }

        public async Task<(bool Success, string Message)> DeleteMeetingMinutes(int id, ApplicationUser currentUser)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var meetingMinutes = await _context.MeetingMinutes
                    .Include(mm => mm.Project)
                    .FirstOrDefaultAsync(mm => mm.Id == id && !mm.IsDeleted);

                if (meetingMinutes == null)
                    return (false, "会议纪要不存在或已被删除");

                if (meetingMinutes.Project.IsDeleted)
                    return (false, "所属项目已删除，无法执行删除操作");

                bool within48Hours = (AppTime.Now - meetingMinutes.CreatedAt) <= TimeSpan.FromHours(48);
                if (!within48Hours)
                    return (false, "超过48小时，无法删除会议纪要");

                bool isCreator = meetingMinutes.CreatorId == currentUser.Id;
                bool isProjectAdmin = await _context.ProjectUsers
                    .AnyAsync(pu => pu.ProjectId == meetingMinutes.ProjectId && pu.UserId == currentUser.Id && pu.ProjectRole == (int)ProjectRole.Admin);
                bool isSystemAdmin = currentUser.Role == UserRole.systemAdmin;

                if (!(isCreator || isProjectAdmin || isSystemAdmin))
                    return (false, "无权限删除此会议纪要");

                var creatorName = await GetCreatorName(meetingMinutes.CreatorId);
                var attachments = await GetAttachmentsByMeetingId(meetingMinutes.Id);
                var attachmentNames = attachments.Select(a => a.FileName).ToList();

                meetingMinutes.IsDeleted = true;
                meetingMinutes.LastModifiedAt = AppTime.Now;
                _context.MeetingMinutes.Update(meetingMinutes);
                await _context.SaveChangesAsync();

                var deleteLog = new ChangeLog
                {
                    OperationType = OperationType.删除,
                    OperationStatus = OperationStatus.成功,
                    OperatedAt = DateTime.UtcNow,
                    OperationTarget = OperationTarget.会议纪要,
                    OperatedByUserId = currentUser.Id,
                    OperatedByUserName = currentUser.UserName,
                    OperatedByRealName = currentUser.RealName,
                    ProjectId = meetingMinutes.ProjectId,
                    ProjectName = meetingMinutes.Project?.Name,
                    TargetId = meetingMinutes.Id,
                    TargetName = meetingMinutes.MeetingTitle,
                    BeforeContent = $"创建人：{creatorName}\n" +
                                    $"标题：{meetingMinutes.MeetingTitle}\n" +
                                    $"报告日期：{meetingMinutes.MeetingDate:yyyy-MM-dd}\n" +
                                    $"创建时间：{meetingMinutes.CreatedAt:yyyy-MM-dd HH:mm}\n" +
                                  (attachmentNames.Any() ? $"附件：{string.Join("、", attachmentNames)}" : "无附件") +
                                  "\n状态：正常",
                    AfterContent = $"状态：已删除"
                };
                _context.ChangeLogs.Add(deleteLog);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();
                return (true, "会议纪要已成功删除");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return (false, $"删除失败：{ex.Message}");
            }
        }
        #endregion

        #region 腾讯会议CLI拉取相关
        public async Task<List<TencentMeetDailyItem>> QueryTencentDailyMeetListAsync(DateTime meetDate)
        {
            return await _tencentCliService.QueryDailyMeetListAsync(meetDate);
        }

        public async Task<(string MergeContent, string RawJsonJoin, string RecordIds)> BatchMergeTencentMeetTranscriptAsync(
            List<string> recordIds,
            Dictionary<string, string>? titleMap = null)
        {
            return await _tencentCliService.BatchFetchAndMergeAsync(recordIds, titleMap);
        }
        #endregion

        #region 工具方法
        private static string CleanMarkdownContent(string content)
        {
            if (string.IsNullOrEmpty(content)) return "无内容";

            var cleaned = Regex.Replace(content, @"[#*>`\-_\[\]]", "", RegexOptions.Multiline);
            cleaned = Regex.Replace(cleaned, @"\!\[.*?\]\(.*?\)", "（图片）", RegexOptions.Multiline);
            cleaned = Regex.Replace(cleaned, @"\n{2,}", "\n", RegexOptions.Multiline);
            return cleaned.Length > 200 ? cleaned.Substring(0, 200) + "..." : cleaned;
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }

        private static string GetFileIconClass(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLower();
            return ext switch
            {
                ".pdf" => "fa-file-pdf text-danger",
                ".doc" or ".docx" => "fa-file-word text-primary",
                ".xls" or ".xlsx" => "fa-file-excel text-success",
                ".ppt" or ".pptx" => "fa-file-powerpoint text-warning",
                ".jpg" or ".jpeg" or ".png" => "fa-file-image text-info",
                ".txt" => "fa-file-alt text-secondary",
                _ => "fa-file text-dark"
            };
        }
        #endregion
    }
}

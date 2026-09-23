using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ToDo.Context;
using ToDo.Domain.AI;
using ToDo.Entities;
using ToDo.Entities.DailySummary;

namespace ToDo.Domain;

/// <summary>
/// 按用户生成独立的每日工作报告。报告只使用系统中能明确关联到该用户的事实数据。
/// </summary>
public sealed class PersonalDailySummaryService
{
    private readonly ApplicationDbContext _context;
    private readonly IAIService _aiService;
    private readonly IEventBus _eventBus;
    private readonly UserNotificationService _notifications;
    private readonly ILogger<PersonalDailySummaryService> _logger;

    public PersonalDailySummaryService(
        ApplicationDbContext context,
        IAIService aiService,
        IEventBus eventBus,
        UserNotificationService notifications,
        ILogger<PersonalDailySummaryService> logger)
    {
        _context = context;
        _aiService = aiService;
        _eventBus = eventBus;
        _notifications = notifications;
        _logger = logger;
    }

    public Task<List<UserSummaryCheckpoint>> GetCheckpointsAsync(CancellationToken cancellationToken = default)
    {
        return _context.UserSummaryCheckpoints.AsNoTracking()
            .Include(item => item.User)
            .Include(item => item.LastDailyWorkSummary)
            .OrderByDescending(item => item.LastSuccessfulSummaryAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<PersonalSummaryBatchResult> GenerateAllAsync(
        DateTime reportDate,
        CancellationToken cancellationToken = default)
    {
        var userIds = await _context.Users.AsNoTracking()
            .Where(user => !user.IsDeleted && user.Status == UserStatus.Active)
            .OrderBy(user => user.Id)
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        var result = new PersonalSummaryBatchResult { TotalUsers = userIds.Count };
        foreach (var userId in userIds)
        {
            try
            {
                var item = await GenerateForUserAsync(userId, reportDate, isAutomatic: true, forceRebuild: false, cancellationToken);
                if (item.Generated) result.GeneratedUsers++;
                else result.SkippedUsers++;
            }
            catch (Exception ex)
            {
                result.FailedUsers++;
                result.Failures.Add($"用户 #{userId}：生成失败");
                _logger.LogError(ex, "用户 {UserId} 个人日报生成失败", userId);
            }
        }

        return result;
    }

    /// <summary>
    /// 在个人日报基础上按项目汇总生成团队汇报（仅项目负责人可见）。
    /// 对每个当日有成员个人日报的项目，聚合各成员的项目分项摘要，调用AI生成团队汇报。
    /// </summary>
    public Task<TeamReportBatchResult> GenerateTeamReportsAsync(
        DateTime reportDate,
        CancellationToken cancellationToken = default) =>
        GenerateTeamReportsCoreAsync(reportDate, projectId: null, cancellationToken);

    public Task<TeamReportBatchResult> GenerateTeamReportAsync(
        int projectId,
        DateTime reportDate,
        CancellationToken cancellationToken = default)
    {
        if (projectId <= 0) throw new ArgumentOutOfRangeException(nameof(projectId));
        return GenerateTeamReportsCoreAsync(reportDate, projectId, cancellationToken);
    }

    private async Task<TeamReportBatchResult> GenerateTeamReportsCoreAsync(
        DateTime reportDate,
        int? projectId,
        CancellationToken cancellationToken)
    {
        var date = reportDate.Date;
        var projectQuery = _context.Project.AsNoTracking()
            .Where(project => !project.IsDeleted && project.Status == ProjectStatus.Active);
        if (projectId.HasValue)
            projectQuery = projectQuery.Where(project => project.Id == projectId.Value);

        var projects = await projectQuery
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);

        var batch = new TeamReportBatchResult { TotalProjects = projects.Count };
        foreach (var project in projects)
        {
            try
            {
                // 1) 该项目的全量成员（项目成员+项目负责人），无论是否有日报
                var memberUserIds = await _context.ProjectUsers.AsNoTracking()
                    .Where(pu => pu.ProjectId == project.Id)
                    .Select(pu => pu.UserId)
                    .ToListAsync(cancellationToken);
                if (project.LeaderUserId != 0 && !memberUserIds.Contains(project.LeaderUserId))
                    memberUserIds.Add(project.LeaderUserId);

                if (memberUserIds.Count == 0)
                {
                    batch.SkippedProjects++;
                    continue;
                }

                // 2) 查找当日该项目的个人日报分项明细（DailyProjectSummaryDetail 按 ProjectId 关联）
                var memberDetails = await
                    (from summary in _context.DailyWorkSummaries.AsNoTracking()
                     where summary.SummaryDate.Date == date && !summary.IsDeleted && summary.UserId.HasValue
                     join detail in _context.DailyProjectSummaryDetails.AsNoTracking()
                         on summary.Id equals detail.DailySummaryId
                     where detail.ProjectId == project.Id && !detail.IsDeleted
                     select new { summary.UserId, summary.TotalSummary, detail.ProjectSummary })
                    .ToListAsync(cancellationToken);

                var allUsers = await _context.Users.AsNoTracking()
                    .Where(u => memberUserIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.UserName, u.RealName })
                    .ToDictionaryAsync(u => u.Id, cancellationToken);

                var detailsByUser = memberDetails.GroupBy(m => m.UserId!.Value)
                    .ToDictionary(g => g.Key, g => g.ToList());

                var leaderName = allUsers.TryGetValue(project.LeaderUserId, out var lu)
                    ? (string.IsNullOrWhiteSpace(lu.RealName) ? lu.UserName ?? $"用户#{lu.Id}" : lu.RealName)
                    : "未设置";

                // 3) 合并：全量成员 + 是否有日报 + 摘要内容
                var members = new List<MemberSummaryInfo>(memberUserIds.Count);
                foreach (var uid in memberUserIds.OrderBy(id => id))
                {
                    var u = allUsers.TryGetValue(uid, out var uu) ? uu : null;
                    var name = u != null
                        ? (string.IsNullOrWhiteSpace(u.RealName) ? u.UserName ?? $"用户#{u.Id}" : u.RealName)
                        : $"用户#{uid}";

                    if (detailsByUser.TryGetValue(uid, out var details) && details.Count > 0)
                    {
                        // 同一成员在本项目下可能有多条分项，合并
                        foreach (var d in details)
                        {
                            members.Add(new MemberSummaryInfo
                            {
                                UserName = name,
                                HasDailyReport = true,
                                ProjectSummary = d.ProjectSummary ?? string.Empty,
                                TotalSummary = d.TotalSummary ?? string.Empty
                            });
                        }
                    }
                    else
                    {
                        // 成员当日没有该项目的个人日报内容，明确标记
                        members.Add(new MemberSummaryInfo
                        {
                            UserName = name,
                            HasDailyReport = false,
                            ProjectSummary = string.Empty,
                            TotalSummary = string.Empty
                        });
                    }
                }

                // 4) 获取项目任务组快照（有分组才生成，无则空列表）
                var taskGroups = new List<TaskGroupSnapshotInfo>();
                var dbGroups = await _context.TaskGroups.AsNoTracking()
                    .Where(g => g.ProjectId == project.Id && !g.IsDeleted)
                    .OrderBy(g => g.Id)
                    .ToListAsync(cancellationToken);
                if (dbGroups.Count > 0)
                {
                    var groupIds = dbGroups.Select(g => g.Id).ToList();
                    // 各分组下的未删除任务
                    var groupTasks = await _context.ToDoTasks.AsNoTracking()
                        .Where(t => groupIds.Contains(t.GroupId ?? 0) && !t.IsDeleted && t.ProjectId == project.Id)
                        .Select(t => new
                        {
                            t.GroupId,
                            t.Title,
                            t.Status,
                            t.Progress,
                            t.AssigneeId
                        })
                        .ToListAsync(cancellationToken);
                    // 任务负责人ID -> 姓名
                    var assigneeIds = groupTasks.Select(t => t.AssigneeId ?? 0).Where(id => id != 0).Distinct().ToList();
                    var assigneeNames = await _context.Users.AsNoTracking()
                        .Where(u => assigneeIds.Contains(u.Id))
                        .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.RealName) ? (u.UserName ?? $"用户#{u.Id}") : u.RealName, cancellationToken);

                    foreach (var g in dbGroups)
                    {
                        var tasks = groupTasks.Where(t => t.GroupId == g.Id).ToList();
                        int total = tasks.Count;
                        int completed = tasks.Count(t => t.Status == ToDo.Entities.TaskStatus.Completed);
                        int progressAvg = total == 0 ? 0 : (int)Math.Round(tasks.Average(t => t.Progress));
                        var snapshots = tasks.Select(t =>
                        {
                            var assigneeName = t.AssigneeId.HasValue && assigneeNames.TryGetValue(t.AssigneeId.Value, out var an)
                                ? an
                                : "未指派";
                            return $"{t.Title}｜{t.Status}｜{t.Progress}%｜{assigneeName}";
                        }).ToList();

                        taskGroups.Add(new TaskGroupSnapshotInfo
                        {
                            GroupName = g.Name,
                            GroupDescription = g.Description ?? string.Empty,
                            TotalTaskCount = total,
                            CompletedTaskCount = completed,
                            OverallProgressPercent = progressAvg,
                            TaskSnapshots = snapshots
                        });
                    }
                }

                var aiResult = await _aiService.GenerateTeamReportAsync(new TeamReportInput
                {
                    ReportDate = date,
                    ProjectId = project.Id,
                    ProjectName = project.Name,
                    ProjectLeader = leaderName,
                    Members = members,
                    TaskGroups = taskGroups
                });

                if (!aiResult.Success || string.IsNullOrWhiteSpace(aiResult.GeneratedReport))
                {
                    batch.FailedProjects++;
                    var reason = string.IsNullOrWhiteSpace(aiResult.ErrorMessage) ? "AI 未返回团队汇报内容" : aiResult.ErrorMessage;
                    batch.Failures.Add($"{project.Name}：生成失败");
                    _logger.LogWarning("项目 {ProjectId} 团队汇报生成失败：{Reason}", project.Id, reason);
                    continue;
                }

                // 复用：若当日已存在该项目的团队汇报则覆盖，避免重复
                var existing = await _context.DailyReport
                    .FirstOrDefaultAsync(r => r.ProjectId == project.Id
                        && r.ReportType == 4
                        && r.ReportDate.Date == date
                        && !r.IsDeleted, cancellationToken);

                if (existing == null)
                {
                    existing = new DailyReport
                    {
                        ProjectId = project.Id,
                        ReportType = 4,
                        ReportDate = date,
                        ReportTitle = $"{project.Name} 团队汇报 {date:yyyy-MM-dd}",
                        ReporterId = project.LeaderUserId,
                        IsSystemGenerated = true
                    };
                    _context.DailyReport.Add(existing);
                }
                else
                {
                    existing.ReportTitle = $"{project.Name} 团队汇报 {date:yyyy-MM-dd}";
                    existing.LastModifiedAt = AppTime.Now;
                }
                existing.ReportContent = aiResult.GeneratedReport;
                await _context.SaveChangesAsync(cancellationToken);
                batch.GeneratedProjects++;
            }
            catch (Exception ex)
            {
                batch.FailedProjects++;
                batch.Failures.Add($"{project.Name}：生成失败");
                _logger.LogError(ex, "项目 {ProjectId} 团队汇报生成失败", project.Id);
            }
        }

        return batch;
    }

    public async Task<PersonalSummaryGenerationResult> GenerateForUserAsync(
        int userId,
        DateTime reportDate,
        bool isAutomatic,
        bool forceRebuild,
        CancellationToken cancellationToken = default)
    {
        var user = await _context.Users.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == userId && !item.IsDeleted && item.Status == UserStatus.Active, cancellationToken)
            ?? throw new InvalidOperationException("用户不存在、已停用或已删除");

        var date = reportDate.Date;
        var cutoff = AppTime.Now;
        var existing = await _context.DailyWorkSummaries
            .FirstOrDefaultAsync(item => item.UserId == userId && item.SummaryDate.Date == date && !item.IsDeleted, cancellationToken);
        var checkpoint = await _context.UserSummaryCheckpoints
            .FirstOrDefaultAsync(item => item.UserId == userId, cancellationToken);

        var start = forceRebuild
            ? date
            : existing?.SummaryStartAt
                ?? checkpoint?.LastSuccessfulSummaryAt
                ?? date;
        if (start > cutoff) start = date;

        var projects = await LoadPersonalActivitiesAsync(user, start, cutoff, cancellationToken);
        if (projects.Count == 0)
        {
            await AdvanceEmptyCheckpointAsync(userId, cutoff, checkpoint, cancellationToken);
            return new PersonalSummaryGenerationResult
            {
                Generated = false,
                Message = $"{user.RealName ?? user.UserName} 在本次时间范围内没有可汇总的个人变化"
            };
        }

        var prompt = BuildPrompt(user, date, start, cutoff, projects);
        var aiContent = await _aiService.GetChatCompletionAsync(
            prompt,
            new AIChatOptions
            {
                Temperature = 0.2,
                MaxTokens = 16000,
                TimeoutSeconds = 180
            },
            cancellationToken);
        if (string.IsNullOrWhiteSpace(aiContent))
            throw new InvalidOperationException("AI 未返回个人日报内容");

        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            if (existing == null)
            {
                existing = new DailyWorkSummary
                {
                    UserId = userId,
                    SummaryDate = date
                };
                _context.DailyWorkSummaries.Add(existing);
            }
            else
            {
                var oldDetails = await _context.DailyProjectSummaryDetails
                    .Where(item => item.DailySummaryId == existing.Id)
                    .ToListAsync(cancellationToken);
                _context.DailyProjectSummaryDetails.RemoveRange(oldDetails);
            }

            existing.Title = $"{date:yyyy-MM-dd} {DisplayName(user)}个人工作报告";
            existing.TotalSummary = aiContent.Trim();
            existing.SummaryStatus = 1;
            existing.SummaryStartAt = start;
            existing.SummaryEndAt = cutoff;
            existing.IsAutomatic = isAutomatic;
            existing.CreateTime = AppTime.Now;
            await _context.SaveChangesAsync(cancellationToken);

            _context.DailyProjectSummaryDetails.AddRange(projects.Select(project => new DailyProjectSummaryDetail
            {
                DailySummaryId = existing.Id,
                ProjectId = project.ProjectId,
                ProjectName = project.ProjectName,
                LeaderUserName = project.LeaderName,
                ProjectTag = "个人动态",
                ProjectSummary = BuildProjectDetail(project),
                CreateTime = AppTime.Now
            }));

            if (checkpoint == null)
            {
                checkpoint = new UserSummaryCheckpoint
                {
                    UserId = userId,
                    Version = 1
                };
                _context.UserSummaryCheckpoints.Add(checkpoint);
            }
            else
            {
                checkpoint.Version++;
            }

            checkpoint.LastSuccessfulSummaryAt = cutoff;
            checkpoint.LastDailyWorkSummary = existing;
            checkpoint.UpdatedAt = AppTime.Now;

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        try
        {
            await _notifications.NotifyAsync(
                userId,
                "个人日报已生成",
                $"{date:yyyy-MM-dd} 的个人工作报告已生成，可进入每日情况汇总查看。",
                "Success",
                $"/DailySummary/Index?selectDate={date:yyyy-MM-dd}");
            await _eventBus.PublishAsync(
                "personal-daily-summary.generated",
                new { userId, summaryId = existing.Id, reportDate = date, start, cutoff, isAutomatic },
                "DailyWorkSummary",
                existing.Id.ToString(),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "用户 {UserId} 个人日报已保存，但通知或事件发布失败", userId);
        }

        return new PersonalSummaryGenerationResult
        {
            Generated = true,
            SummaryId = existing.Id,
            Message = $"{DisplayName(user)}的个人日报已生成"
        };
    }

    private async Task<List<PersonalProjectActivity>> LoadPersonalActivitiesAsync(
        ApplicationUser user,
        DateTime start,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        var projectQuery = _context.Project.AsNoTracking()
            .Where(project => !project.IsDeleted && project.Status == ProjectStatus.Active);
        if (user.Role != UserRole.systemAdmin)
        {
            projectQuery = projectQuery.Where(project => project.LeaderUserId == user.Id
                || _context.ProjectUsers.Any(member => member.ProjectId == project.Id && member.UserId == user.Id));
        }

        var projects = await projectQuery
            .Select(project => new
            {
                project.Id,
                project.Name,
                LeaderName = project.LeaderUser != null
                    ? project.LeaderUser.RealName ?? project.LeaderUser.UserName
                    : "未设置"
            })
            .OrderBy(project => project.Name)
            .ToListAsync(cancellationToken);

        var results = new List<PersonalProjectActivity>();
        foreach (var project in projects)
        {
            var item = new PersonalProjectActivity
            {
                ProjectId = project.Id,
                ProjectName = project.Name,
                LeaderName = project.LeaderName ?? "未设置"
            };

            var tasks = await _context.ToDoTasks.AsNoTracking()
                .Where(task => task.ProjectId == project.Id && !task.IsDeleted
                    && (task.CreatorId == user.Id || task.AssigneeId == user.Id)
                    && ((task.CreatedAt > start && task.CreatedAt <= cutoff)
                        || (task.UpdatedAt > start && task.UpdatedAt <= cutoff)))
                .OrderByDescending(task => task.UpdatedAt)
                .Take(50)
                .Select(task => new
                {
                    task.Id,
                    task.Title,
                    task.Status,
                    task.Progress,
                    task.StartTime,
                    task.EndTime,
                    task.CreatorId,
                    task.AssigneeId,
                    task.CreatedAt,
                    task.UpdatedAt,
                    task.GroupId
                })
                .ToListAsync(cancellationToken);

            // 按风险/状态分类任务，并标注【新建/今日更新】
            var today = cutoff.Date;
            var overdueTasks = new List<string>();
            var nearDeadlineTasks = new List<string>();
            var halfTimeBehindTasks = new List<string>();
            var normalTasks = new List<string>();
            var completedTasks = new List<string>();
            var taskGroupNewTaskNotes = new List<string>(); // 临时记录任务组内新建任务动态

            foreach (var task in tasks)
            {
                // 1) 任务变化标签：【新建】/【今日更新】/【我创建，被他人更新】/【我负责，被他人更新】
                var isCreatedToday = task.CreatedAt > start && task.CreatedAt <= cutoff;
                var isUpdatedOnly = !isCreatedToday && task.UpdatedAt > start && task.UpdatedAt <= cutoff;
                string changeTag;
                if (isCreatedToday && task.CreatorId == user.Id)
                    changeTag = "【新建任务】";
                else if (isCreatedToday && task.CreatorId != user.Id)
                    changeTag = "【任务指派给我】"; // 别人创建，当天分配给我
                else if (isUpdatedOnly && task.AssigneeId == user.Id && task.CreatorId == user.Id)
                    changeTag = "【我更新】";
                else if (isUpdatedOnly && task.AssigneeId == user.Id)
                    changeTag = "【我负责的任务有更新】";
                else
                    changeTag = "【我创建的任务有更新】";

                var baseInfo = $"{changeTag} #{task.Id} {task.Title}｜{task.Status}｜进度 {task.Progress}%｜截止 {task.EndTime?.ToString("yyyy-MM-dd") ?? "未设置"}";
                var category = ClassifyTask(task.Status, task.Progress, task.StartTime, task.EndTime, today);
                switch (category)
                {
                    case TaskRiskCategory.Overdue:
                        overdueTasks.Add(baseInfo);
                        break;
                    case TaskRiskCategory.NearDeadline:
                        nearDeadlineTasks.Add(baseInfo);
                        break;
                    case TaskRiskCategory.HalfTimeBehind:
                        halfTimeBehindTasks.Add(baseInfo);
                        break;
                    case TaskRiskCategory.Completed:
                        completedTasks.Add(baseInfo);
                        break;
                    default:
                        normalTasks.Add(baseInfo);
                        break;
                }

                // 任务组动态：今日新建且归属任务组的任务
                if (isCreatedToday && task.GroupId.HasValue && task.GroupId.Value != 0)
                {
                    var group = await _context.TaskGroups.AsNoTracking()
                        .FirstOrDefaultAsync(g => g.Id == task.GroupId.Value && !g.IsDeleted, cancellationToken);
                    var groupName = group != null ? group.Name : $"组#{task.GroupId.Value}";
                    taskGroupNewTaskNotes.Add(
                        $"新建任务 #{task.Id}「{task.Title}」归属任务组「{groupName}」");
                }
            }

            // 分类存入 TaskNotes（带分类标题），供AI按分类总结
            var allTaskNotes = new List<string>();
            if (overdueTasks.Count > 0)
            {
                allTaskNotes.Add($"【已逾期｜共{overdueTasks.Count}条】");
                allTaskNotes.AddRange(overdueTasks);
            }
            if (nearDeadlineTasks.Count > 0)
            {
                allTaskNotes.Add($"【临近截止（3天内）｜共{nearDeadlineTasks.Count}条】");
                allTaskNotes.AddRange(nearDeadlineTasks);
            }
            if (halfTimeBehindTasks.Count > 0)
            {
                allTaskNotes.Add($"【时间过半进度不足｜共{halfTimeBehindTasks.Count}条】");
                allTaskNotes.AddRange(halfTimeBehindTasks);
            }
            if (normalTasks.Count > 0)
            {
                allTaskNotes.Add($"【正常进行中｜共{normalTasks.Count}条】");
                allTaskNotes.AddRange(normalTasks);
            }
            if (completedTasks.Count > 0)
            {
                allTaskNotes.Add($"【已完成｜共{completedTasks.Count}条】");
                allTaskNotes.AddRange(completedTasks);
            }
            item.TaskNotes = allTaskNotes;

            // 任务组动态 - 我今天创建的任务组
            var myNewGroups = await _context.TaskGroups.AsNoTracking()
                .Where(g => g.ProjectId == project.Id && !g.IsDeleted
                    && g.CreatorId == user.Id
                    && g.CreatedAt > start && g.CreatedAt <= cutoff)
                .OrderByDescending(g => g.CreatedAt)
                .Take(20)
                .Select(g => new { g.Id, g.Name, g.Description })
                .ToListAsync(cancellationToken);
            var groupNotes = myNewGroups.Select(g =>
                $"【新建任务组】#{g.Id}「{g.Name}」{(!string.IsNullOrWhiteSpace(g.Description) ? $"｜说明：{g.Description}" : string.Empty)}").ToList();
            groupNotes.AddRange(taskGroupNewTaskNotes);
            item.TaskGroupNotes = groupNotes;

            var comments = await _context.TaskComments.AsNoTracking()
                .Where(comment => comment.AuthorId == user.Id
                    && comment.CreatedAt > start && comment.CreatedAt <= cutoff
                    && comment.Task != null && comment.Task.ProjectId == project.Id)
                .OrderByDescending(comment => comment.CreatedAt)
                .Take(30)
                .Select(comment => new { comment.Id, comment.TaskId, comment.Content, comment.CreatedAt, TaskTitle = comment.Task!.Title })
                .ToListAsync(cancellationToken);
            item.CommentNotes = comments.Select(comment =>
                $"任务 #{comment.TaskId}「{comment.TaskTitle}」 我发表评论：{comment.Content}（{comment.CreatedAt:HH:mm}）").ToList();

            var meetings = await _context.MeetingMinutes.AsNoTracking()
                .Where(meeting => meeting.ProjectId == project.Id && meeting.CreatorId == user.Id && !meeting.IsDeleted
                    && ((meeting.CreatedAt > start && meeting.CreatedAt <= cutoff)
                        || (meeting.LastModifiedAt > start && meeting.LastModifiedAt <= cutoff)))
                .OrderByDescending(meeting => meeting.LastModifiedAt)
                .Take(20)
                .Select(meeting => new
                {
                    meeting.Id,
                    meeting.MeetingTitle,
                    meeting.MeetingDate,
                    meeting.CreatedAt,
                    meeting.LastModifiedAt
                })
                .ToListAsync(cancellationToken);
            // 统计每个会议的行动项数量（独立查询，避免依赖导航属性结构）
            var meetingIds = meetings.Select(m => m.Id).Distinct().ToList();
            Dictionary<int, int> actionCountMap;
            if (meetingIds.Count > 0)
            {
                actionCountMap = await _context.MeetingActionItems.AsNoTracking()
                    .Where(a => meetingIds.Contains(a.MeetingMinutesId))
                    .GroupBy(a => a.MeetingMinutesId)
                    .Select(g => new { MeetingId = g.Key, Count = g.Count() })
                    .ToDictionaryAsync(x => x.MeetingId, x => x.Count, cancellationToken);
            }
            else
            {
                actionCountMap = new Dictionary<int, int>();
            }
            item.MeetingNotes = meetings.Select(m =>
            {
                var isNew = m.CreatedAt > start && m.CreatedAt <= cutoff;
                string actionTag = isNew ? "【新建会议纪要】" : "【今日更新会议纪要】";
                var extras = new List<string>();
                if (actionCountMap.TryGetValue(m.Id, out var ac) && ac > 0)
                    extras.Add($"行动项 {ac} 条");
                var extraStr = extras.Count > 0 ? "｜" + string.Join("，", extras) : string.Empty;
                return $"{actionTag} #{m.Id} {m.MeetingTitle}｜会议日期 {m.MeetingDate:yyyy-MM-dd}{extraStr}";
            }).ToList();

            var actions = await _context.MeetingActionItems.AsNoTracking()
                .Where(action => action.AssigneeId == user.Id
                    && action.CreatedAt > start && action.CreatedAt <= cutoff
                    && action.MeetingMinutes != null && action.MeetingMinutes.ProjectId == project.Id)
                .OrderByDescending(action => action.CreatedAt)
                .Take(30)
                .Select(action => new
                {
                    action.Id,
                    action.Title,
                    action.TaskStatus,
                    action.Deadline,
                    action.MeetingMinutesId,
                    MeetingTitle = action.MeetingMinutes != null ? action.MeetingMinutes.MeetingTitle : string.Empty,
                    action.CreatedAt
                })
                .ToListAsync(cancellationToken);
            var actionNotes = actions.Select(action =>
                $"【指派给我的行动项】#{action.Id} {action.Title}｜{action.TaskStatus}｜截止 {action.Deadline?.ToString("yyyy-MM-dd") ?? "未设置"}｜来源：{action.MeetingTitle}（{action.CreatedAt:HH:mm}）").ToList();

            // 本人创建的行动项（本人是会议纪要创建者，当天该会议下新增的行动项——即使指派给别人也算我产出）
            var createdActions = await (from action in _context.MeetingActionItems.AsNoTracking()
                                        join meeting in _context.MeetingMinutes.AsNoTracking()
                                            on action.MeetingMinutesId equals meeting.Id
                                        where meeting.ProjectId == project.Id && !meeting.IsDeleted
                                            && meeting.CreatorId == user.Id
                                            && action.CreatedAt > start && action.CreatedAt <= cutoff
                                            // 避免和上面「指派给我」的重复
                                            && action.AssigneeId != user.Id
                                        orderby action.CreatedAt descending
                                        select new
                                        {
                                            action.Id,
                                            action.Title,
                                            action.TaskStatus,
                                            action.Deadline,
                                            action.CreatedAt,
                                            MeetingTitle = meeting.MeetingTitle,
                                            AssigneeRealName = action.Assignee != null ? action.Assignee.RealName : null,
                                            AssigneeUserName = action.Assignee != null ? action.Assignee.UserName : null,
                                            action.AssigneeId,
                                            action.AssigneeText
                                        }).Take(30).ToListAsync(cancellationToken);
            actionNotes.AddRange(createdActions.Select(a =>
            {
                string assignee;
                if (a.AssigneeId.HasValue && a.AssigneeId.Value != 0)
                    assignee = !string.IsNullOrWhiteSpace(a.AssigneeRealName)
                        ? a.AssigneeRealName
                        : (!string.IsNullOrWhiteSpace(a.AssigneeUserName) ? a.AssigneeUserName : $"用户#{a.AssigneeId.Value}");
                else
                    assignee = string.IsNullOrWhiteSpace(a.AssigneeText) ? "未指派" : a.AssigneeText;
                return $"【我创建的行动项】#{a.Id} {a.Title}｜{a.TaskStatus}｜截止 {a.Deadline?.ToString("yyyy-MM-dd") ?? "未设置"}｜负责人：{assignee}｜来源：{a.MeetingTitle}（{a.CreatedAt:HH:mm}）";
            }));
            item.ActionNotes = actionNotes;

            var documents = await _context.ProjectDocuments.AsNoTracking()
                .Where(document => document.ProjectId == project.Id && document.UploadedById == user.Id
                    && document.UploadedAt > start && document.UploadedAt <= cutoff)
                .OrderByDescending(document => document.UploadedAt)
                .Take(20)
                .Select(document => new { document.FileName, document.VersionNumber, document.CategoryName, document.Category })
                .ToListAsync(cancellationToken);
            item.DocumentNotes = documents.Select(document =>
                $"{document.FileName}｜版本 v{document.VersionNumber}｜分类 {document.CategoryName ?? document.Category.GetDisplayName()}").ToList();

            var reports = await _context.DailyReport.AsNoTracking()
                .Where(report => report.ProjectId == project.Id && report.ReporterId == user.Id
                    && !report.IsDeleted && !report.IsSystemGenerated
                    && ((report.CreatedAt > start && report.CreatedAt <= cutoff)
                        || (report.LastModifiedAt > start && report.LastModifiedAt <= cutoff)))
                .OrderByDescending(report => report.LastModifiedAt)
                .Take(20)
                .Select(report => new { report.Id, report.ReportTitle, report.ReportType })
                .ToListAsync(cancellationToken);
            item.ReportNotes = reports.Select(report =>
                $"#{report.Id} {report.ReportTitle}｜{(report.ReportType == 1 ? "日报" : report.ReportType == 2 ? "周报" : "月报")}").ToList();

            NormalizeNotes(item);
            if (item.HasActivity) results.Add(item);
        }

        return results;
    }

    private async Task AdvanceEmptyCheckpointAsync(
        int userId,
        DateTime cutoff,
        UserSummaryCheckpoint? checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint == null)
        {
            _context.UserSummaryCheckpoints.Add(new UserSummaryCheckpoint
            {
                UserId = userId,
                LastSuccessfulSummaryAt = cutoff,
                UpdatedAt = AppTime.Now,
                Version = 1
            });
        }
        else
        {
            checkpoint.LastSuccessfulSummaryAt = cutoff;
            checkpoint.UpdatedAt = AppTime.Now;
            checkpoint.Version++;
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static string BuildPrompt(
        ApplicationUser user,
        DateTime reportDate,
        DateTime start,
        DateTime cutoff,
        IReadOnlyCollection<PersonalProjectActivity> projects)
    {
        var builder = new StringBuilder();
        builder.AppendLine("你是个人工作日报助手。请只根据以下系统事实生成个人日报，禁止编造完成情况、负责人、风险或计划。")
            .AppendLine($"人员：{DisplayName(user)}")
            .AppendLine($"报告日期：{reportDate:yyyy-MM-dd}")
            .AppendLine($"事实时间范围：{start:yyyy-MM-dd HH:mm:ss} 至 {cutoff:yyyy-MM-dd HH:mm:ss}")
            .AppendLine("本人创建或负责的任务发生变化，只能表述为“本人相关任务发生变化”，不能推断一定由本人操作。")
            .AppendLine("请使用 Markdown，依次输出：今日完成、进行中、本人主动产出、风险与阻塞、下一步建议、项目明细。没有证据的栏目写“暂无系统记录”。")
            .AppendLine()
            .AppendLine("【本人主动产出 强制规则（违反即不合格）】：")
            .AppendLine("- 这一栏必须汇总『我今天实际做了什么』，只要下面任何一类有事实，必须逐条列出来，禁止写“暂无”糊弄：")
            .AppendLine("  · 本人新建的任务、本人更新的任务、本人负责的任务发生变动；")
            .AppendLine("  · 本人新建任务组、把任务归属到任务组（新增的组任务/组认领）；")
            .AppendLine("  · 本人在任意任务下发表的评论；")
            .AppendLine("  · 本人新建或今日更新的会议纪要（含行动项条数）；")
            .AppendLine("  · 本人创建的会议行动项（不管指派给谁）以及指派给本人的行动项；")
            .AppendLine("  · 本人上传的项目资料（文件名、版本、分类）；")
            .AppendLine("  · 本人提交的非系统自动生成的日报/周报/月报。")
            .AppendLine("- 每一条要简短写清楚对象编号、名称、产出内容要点，不要写成『无』或『暂无』。")
            .AppendLine()
            .AppendLine("【任务分类处理强制规则（违反即不合格）】：")
            .AppendLine("- 系统已将任务按风险预分类，每个【...】标签代表一类：已逾期 / 临近截止（3天内到期） / 时间过半进度不足（工期过50%但进度<50%） / 正常进行中 / 已完成")
            .AppendLine("- 任务列表必须严格按分类分块输出，不要混排。分类标题要保留【】标签样式，块内列出该类任务。")
            .AppendLine("- 在“进行中”板块中，按上述分类分节展示（已逾期 → 临近截止 → 时间过半进度不足 → 正常进行中），已完成任务归入“今日完成”板块。")
            .AppendLine("- “风险与阻塞”板块必须列出所有已逾期、临近截止、时间过半进度不足的任务（如果有），每类单独成节，写清楚任务编号、名称、截止时间/进度现状、风险后果、建议动作；没有风险再写“暂无”。")
            .AppendLine("- 不要忽略“时间过半进度不足”类任务，它们是隐性风险，必须在风险板块明确指出。")
            .AppendLine();

        foreach (var project in projects)
        {
            builder.AppendLine($"【项目：{project.ProjectName}】");
            AppendNotes(builder, "本人创建或负责的任务变化（已按风险预分类，已标注【新建任务】/【我更新】/【我负责的任务有更新】等变化类型）", project.TaskNotes);
            AppendNotes(builder, "任务组动态（新建任务组、新建任务归属任务组）", project.TaskGroupNotes);
            AppendNotes(builder, "本人评论（我今天在任务下发表的评论）", project.CommentNotes);
            AppendNotes(builder, "本人创建或维护的会议纪要（新建/今日更新，含行动项条数）", project.MeetingNotes);
            AppendNotes(builder, "分配给本人的会议行动项 + 本人创建的行动项（含指派给谁）", project.ActionNotes);
            AppendNotes(builder, "本人上传的项目资料", project.DocumentNotes);
            AppendNotes(builder, "本人提交的非系统报告", project.ReportNotes);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildProjectDetail(PersonalProjectActivity project)
    {
        var builder = new StringBuilder();
        AppendNotes(builder, "任务", project.TaskNotes);
        AppendNotes(builder, "任务组动态", project.TaskGroupNotes);
        AppendNotes(builder, "本人评论", project.CommentNotes);
        AppendNotes(builder, "会议", project.MeetingNotes);
        AppendNotes(builder, "行动项", project.ActionNotes);
        AppendNotes(builder, "资料", project.DocumentNotes);
        AppendNotes(builder, "报告", project.ReportNotes);
        return builder.ToString().Trim();
    }

    private static void AppendNotes(StringBuilder builder, string title, IReadOnlyCollection<string> notes)
    {
        if (notes.Count == 0) return;
        builder.AppendLine($"{title}：");
        foreach (var note in notes) builder.AppendLine($"- {note}");
    }

    private static void NormalizeNotes(PersonalProjectActivity item)
    {
        item.CommentNotes = item.CommentNotes.Select(note => Limit(note, 500)).ToList();
        item.TaskGroupNotes = item.TaskGroupNotes.Select(note => Limit(note, 500)).ToList();
        item.TaskNotes = item.TaskNotes.Select(note => Limit(note, 500)).ToList();
        item.MeetingNotes = item.MeetingNotes.Select(note => Limit(note, 500)).ToList();
        item.ActionNotes = item.ActionNotes.Select(note => Limit(note, 500)).ToList();
        item.DocumentNotes = item.DocumentNotes.Select(note => Limit(note, 500)).ToList();
        item.ReportNotes = item.ReportNotes.Select(note => Limit(note, 500)).ToList();
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static string DisplayName(ApplicationUser user) =>
        string.IsNullOrWhiteSpace(user.RealName) ? user.UserName ?? $"用户 #{user.Id}" : user.RealName;

    /// <summary>
    /// 个人日报任务风险分类
    /// </summary>
    private enum TaskRiskCategory
    {
        /// <summary>已完成</summary>
        Completed,
        /// <summary>已逾期：未完成 + 截止时间早于今日</summary>
        Overdue,
        /// <summary>临近截止（3天内到期）：未完成 + 截止时间在[今日, 今日+3天]区间内</summary>
        NearDeadline,
        /// <summary>时间过半进度不足：未完成 + 有开始/截止时间，工期已过50%但进度&lt;50%</summary>
        HalfTimeBehind,
        /// <summary>正常进行中（未完成且不属于以上几类）</summary>
        Normal
    }

    /// <summary>
    /// 根据任务状态、进度、起止时间判断风险分类
    /// </summary>
    private static TaskRiskCategory ClassifyTask(
        ToDo.Entities.TaskStatus status,
        int progress,
        DateTime? startTime,
        DateTime? endTime,
        DateTime today)
    {
        if (status == ToDo.Entities.TaskStatus.Completed)
            return TaskRiskCategory.Completed;

        // 1) 已逾期
        if (endTime.HasValue && endTime.Value.Date < today.Date)
            return TaskRiskCategory.Overdue;

        // 2) 临近截止（3天内）
        if (endTime.HasValue && endTime.Value.Date >= today.Date
            && endTime.Value.Date <= today.Date.AddDays(3))
            return TaskRiskCategory.NearDeadline;

        // 3) 时间过半进度不足（需要有开始和截止时间）
        if (startTime.HasValue && endTime.HasValue
            && endTime.Value > startTime.Value
            && today.Date > startTime.Value.Date)
        {
            var totalDuration = (endTime.Value.Date - startTime.Value.Date).TotalDays;
            var elapsedDuration = (today.Date - startTime.Value.Date).TotalDays;
            if (totalDuration > 0 && elapsedDuration / totalDuration >= 0.5 && progress < 50)
                return TaskRiskCategory.HalfTimeBehind;
        }

        return TaskRiskCategory.Normal;
    }
}

public sealed class PersonalProjectActivity
{
    public int ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public string LeaderName { get; init; } = string.Empty;
    public List<string> TaskNotes { get; set; } = [];
    public List<string> CommentNotes { get; set; } = [];
    public List<string> TaskGroupNotes { get; set; } = [];
    public List<string> MeetingNotes { get; set; } = [];
    public List<string> ActionNotes { get; set; } = [];
    public List<string> DocumentNotes { get; set; } = [];
    public List<string> ReportNotes { get; set; } = [];
    public bool HasActivity => TaskNotes.Count + CommentNotes.Count + TaskGroupNotes.Count
        + MeetingNotes.Count + ActionNotes.Count
        + DocumentNotes.Count + ReportNotes.Count > 0;
}

public sealed class PersonalSummaryGenerationResult
{
    public bool Generated { get; init; }
    public int? SummaryId { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class PersonalSummaryBatchResult
{
    public int TotalUsers { get; set; }
    public int GeneratedUsers { get; set; }
    public int SkippedUsers { get; set; }
    public int FailedUsers { get; set; }
    public List<string> Failures { get; } = [];

    public string ToDisplayText()
    {
        var summary = $"共 {TotalUsers} 人：生成 {GeneratedUsers}，无变化跳过 {SkippedUsers}，失败 {FailedUsers}";
        return Failures.Count == 0 ? summary : summary + "；" + string.Join("；", Failures.Take(5));
    }
}

public sealed class TeamReportBatchResult
{
    public int TotalProjects { get; set; }
    public int GeneratedProjects { get; set; }
    public int SkippedProjects { get; set; }
    public int FailedProjects { get; set; }
    public List<string> Failures { get; } = [];

    public string ToDisplayText()
    {
        var summary = $"共 {TotalProjects} 个项目：生成 {GeneratedProjects}，无成员日报跳过 {SkippedProjects}，失败 {FailedProjects}";
        return Failures.Count == 0 ? summary : summary + "；" + string.Join("；", Failures.Take(5));
    }
}

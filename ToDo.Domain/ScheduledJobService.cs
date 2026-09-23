using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ToDo.Context;
using ToDo.Domain.AI;
using ToDo.Entities;

namespace ToDo.Domain;

public class ScheduledJobService
{
    private readonly ApplicationDbContext _context;
    private readonly IAIService _aiService;
    private readonly IEventBus _eventBus;
    private readonly UserNotificationService _notifications;
    private readonly PersonalDailySummaryService _personalSummaries;
    private readonly ILogger<ScheduledJobService> _logger;

    public ScheduledJobService(
        ApplicationDbContext context,
        IAIService aiService,
        IEventBus eventBus,
        UserNotificationService notifications,
        PersonalDailySummaryService personalSummaries,
        ILogger<ScheduledJobService> logger)
    {
        _context = context;
        _aiService = aiService;
        _eventBus = eventBus;
        _notifications = notifications;
        _personalSummaries = personalSummaries;
        _logger = logger;
    }

    public Task<List<ScheduledJob>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return _context.ScheduledJobs.AsNoTracking().OrderBy(j => j.RunAt).ThenBy(j => j.Name).ToListAsync(cancellationToken);
    }

    public Task<List<ProjectSummaryCheckpoint>> GetSummaryCheckpointsAsync(CancellationToken cancellationToken = default)
    {
        return _context.ProjectSummaryCheckpoints
            .AsNoTracking()
            .Include(item => item.Project)
            .Include(item => item.LastDailyReport)
            .OrderByDescending(item => item.LastSuccessfulSummaryAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<TeamReportCheckpoint>> GetTeamReportCheckpointsAsync(CancellationToken cancellationToken = default)
    {
        // 每个项目最近一次成功生成的团队汇报（ReportType = 4）
        var latestIds = await _context.DailyReport
            .AsNoTracking()
            .Where(r => r.ReportType == 4 && !r.IsDeleted)
            .GroupBy(r => r.ProjectId)
            .Select(g => g.OrderByDescending(r => r.CreatedAt).Select(r => r.Id).FirstOrDefault())
            .Where(id => id != 0)
            .ToListAsync(cancellationToken);

        var reports = await _context.DailyReport
            .AsNoTracking()
            .Include(r => r.Project)
            .Where(r => latestIds.Contains(r.Id))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        return reports.Select(r => new TeamReportCheckpoint
        {
            ProjectId = r.ProjectId,
            ProjectName = r.Project != null ? r.Project.Name : $"项目 #{r.ProjectId}",
            LastSuccessfulSummaryAt = r.CreatedAt,
            LastTeamReportId = r.Id,
            LastTeamReportTitle = r.ReportTitle
        }).ToList();
    }

    public async Task<ScheduledJob> CreateDailyReportJobAsync(
        string name,
        TimeSpan runAt,
        int createdById,
        int? projectId = null,
        int? userId = null,
        CancellationToken cancellationToken = default)
    {
        if (runAt < TimeSpan.Zero || runAt >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(runAt));
        }

        var job = new ScheduledJob
        {
            JobKey = $"daily-report-{Guid.NewGuid():N}",
            Name = string.IsNullOrWhiteSpace(name) ? "每日自动日报" : name.Trim(),
            RunAt = runAt,
            CreatedById = createdById,
            ProjectId = projectId,
            UserId = userId,
            JobType = ScheduledJobType.DailyReport,
            IsEnabled = true,
            NextRunAt = GetNextRun(AppTime.Now, runAt)
        };
        _context.ScheduledJobs.Add(job);
        await _context.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task<ScheduledJob> CreatePersonalDailySummaryJobAsync(
        string name,
        TimeSpan runAt,
        int createdById,
        CancellationToken cancellationToken = default)
    {
        if (runAt < TimeSpan.Zero || runAt >= TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(runAt));

        var job = new ScheduledJob
        {
            JobKey = $"personal-daily-summary-{Guid.NewGuid():N}",
            Name = string.IsNullOrWhiteSpace(name) ? "每个人的每日报告" : name.Trim(),
            RunAt = runAt,
            CreatedById = createdById,
            JobType = ScheduledJobType.PersonalDailySummary,
            IsEnabled = true,
            NextRunAt = GetNextRun(AppTime.Now, runAt)
        };
        _context.ScheduledJobs.Add(job);
        await _context.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task<ScheduledJob> CreateTeamDailySummaryJobAsync(
        string name,
        TimeSpan runAt,
        int createdById,
        CancellationToken cancellationToken = default)
    {
        if (runAt < TimeSpan.Zero || runAt >= TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(runAt));

        var job = new ScheduledJob
        {
            JobKey = $"team-daily-summary-{Guid.NewGuid():N}",
            Name = string.IsNullOrWhiteSpace(name) ? "项目团队汇报" : name.Trim(),
            RunAt = runAt,
            CreatedById = createdById,
            JobType = ScheduledJobType.TeamDailySummary,
            IsEnabled = true,
            NextRunAt = GetNextRun(AppTime.Now, runAt)
        };
        _context.ScheduledJobs.Add(job);
        await _context.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task EnsureDefaultAsync(int createdById, CancellationToken cancellationToken = default)
    {
        if (!await _context.ScheduledJobs.AnyAsync(j => j.JobType == ScheduledJobType.DailyReport, cancellationToken))
            await CreateDailyReportJobAsync("每日自动日报", new TimeSpan(18, 0, 0), createdById, cancellationToken: cancellationToken);
        if (!await _context.ScheduledJobs.AnyAsync(j => j.JobType == ScheduledJobType.PersonalDailySummary, cancellationToken))
            await CreatePersonalDailySummaryJobAsync("每个人的每日报告", new TimeSpan(18, 10, 0), createdById, cancellationToken);
    }

    public async Task ToggleAsync(int id, CancellationToken cancellationToken = default)
    {
        var job = await _context.ScheduledJobs.FindAsync(new object[] { id }, cancellationToken);
        if (job == null) return;
        job.IsEnabled = !job.IsEnabled;
        job.UpdatedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task RunNowAsync(int id, CancellationToken cancellationToken = default)
    {
        var token = Guid.NewGuid().ToString("N");
        var now = AppTime.Now;
        var claimed = await _context.ScheduledJobs
            .Where(job => job.Id == id && !job.IsRunning)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.IsRunning, true)
                .SetProperty(job => job.LockedAt, now)
                .SetProperty(job => job.LockToken, token), cancellationToken);
        if (claimed != 1)
        {
            var exists = await _context.ScheduledJobs.AsNoTracking().AnyAsync(job => job.Id == id, cancellationToken);
            throw new InvalidOperationException(exists ? "定时任务正在执行，请勿重复触发" : "定时任务不存在");
        }
        await ExecuteClaimedJobAsync(id, token, AppTime.Today, cancellationToken);
    }

    public async Task RunDueJobsAsync(CancellationToken cancellationToken)
    {
        var now = AppTime.Now;
        try
        {
            await _notifications.GenerateTaskRemindersAsync(now, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "任务到期提醒生成失败");
        }

        await RecoverStaleJobsAsync(now, cancellationToken);
        for (var index = 0; index < 10; index++)
        {
            var claimed = await ClaimDueJobAsync(now, cancellationToken);
            if (claimed == null) break;
            await ExecuteClaimedJobAsync(claimed.Value.Id, claimed.Value.Token, now.Date, cancellationToken);
        }
    }

    private async Task<(int Id, string Token)?> ClaimDueJobAsync(DateTime now, CancellationToken cancellationToken)
    {
        var id = await _context.ScheduledJobs.AsNoTracking()
            .Where(job => job.IsEnabled && !job.IsRunning
                && (!job.NextRunAt.HasValue || job.NextRunAt <= now))
            .OrderBy(job => job.NextRunAt)
            .ThenBy(job => job.Id)
            .Select(job => (int?)job.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (!id.HasValue) return null;
        var token = Guid.NewGuid().ToString("N");
        var affected = await _context.ScheduledJobs
            .Where(job => job.Id == id.Value && job.IsEnabled && !job.IsRunning
                && (!job.NextRunAt.HasValue || job.NextRunAt <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.IsRunning, true)
                .SetProperty(job => job.LockedAt, now)
                .SetProperty(job => job.LockToken, token), cancellationToken);
        return affected == 1 ? (id.Value, token) : null;
    }

    private async Task ExecuteClaimedJobAsync(int id, string token, DateTime reportDate, CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        var job = await _context.ScheduledJobs.FirstOrDefaultAsync(item => item.Id == id
            && item.IsRunning && item.LockToken == token, cancellationToken);
        if (job == null) return;
        try
        {
            job.LastResult = await RunJobAsync(job, reportDate, cancellationToken);
            var completedAt = AppTime.Now;
            job.LastRunAt = completedAt;
            job.NextRunAt = GetNextRun(completedAt, job.RunAt);
            job.ConsecutiveFailureCount = 0;
            job.LastError = string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job.NextRunAt = AppTime.Now;
            job.LastError = "服务停止，任务已释放等待下次执行";
            throw;
        }
        catch (Exception ex)
        {
            job.ConsecutiveFailureCount++;
            job.LastError = ex.Message.Length <= 2000 ? ex.Message : ex.Message[..2000];
            job.LastResult = $"执行失败：{job.LastError}";
            job.NextRunAt = AppTime.Now.Add(GetFailureRetryDelay(job.ConsecutiveFailureCount));
            _logger.LogError(ex, "定时任务执行失败：{JobKey}，将在 {NextRunAt} 重试", job.JobKey, job.NextRunAt);
        }
        finally
        {
            job.IsRunning = false;
            job.LockedAt = null;
            job.LockToken = string.Empty;
            job.UpdatedAt = AppTime.Now;
            await _context.SaveChangesAsync(CancellationToken.None);
        }
    }

    private Task RecoverStaleJobsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var staleBefore = now.AddHours(-2);
        return _context.ScheduledJobs
            .Where(job => job.IsRunning && job.LockedAt.HasValue && job.LockedAt < staleBefore)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.IsRunning, false)
                .SetProperty(job => job.LockedAt, (DateTime?)null)
                .SetProperty(job => job.LockToken, string.Empty)
                .SetProperty(job => job.NextRunAt, now)
                .SetProperty(job => job.LastError, "上次执行中断，已自动释放锁"), cancellationToken);
    }

    internal static TimeSpan GetFailureRetryDelay(int failureCount) => failureCount switch
    {
        <= 1 => TimeSpan.FromMinutes(5),
        2 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromHours(1)
    };

    private async Task<string> RunJobAsync(ScheduledJob job, DateTime reportDate, CancellationToken cancellationToken)
    {
        if (job.JobType == ScheduledJobType.PersonalDailySummary)
        {
            var result = await _personalSummaries.GenerateAllAsync(reportDate, cancellationToken);
            // 个人日报全部生成完毕后，自动汇总团队汇报
            var teamResult = await _personalSummaries.GenerateTeamReportsAsync(reportDate, cancellationToken);
            return $"{result.ToDisplayText()}；团队汇报：{teamResult.ToDisplayText()}";
        }

        if (job.JobType == ScheduledJobType.TeamDailySummary)
        {
            var result = await _personalSummaries.GenerateTeamReportsAsync(reportDate, cancellationToken);
            return result.ToDisplayText();
        }

        await RunDailyReportAsync(job, reportDate, cancellationToken);
        return "执行成功";
    }

    private async Task RunDailyReportAsync(ScheduledJob job, DateTime reportDate, CancellationToken cancellationToken)
    {
        var summaryCutoff = AppTime.Now;
        var projectsQuery = _context.Project.Where(p => !p.IsDeleted && p.Status == ProjectStatus.Active);
        if (job.ProjectId.HasValue)
        {
            projectsQuery = projectsQuery.Where(p => p.Id == job.ProjectId.Value);
        }

        var projects = await projectsQuery.AsNoTracking().ToListAsync(cancellationToken);
        var failures = new List<string>();

        foreach (var project in projects)
        {
            var checkpoint = await _context.ProjectSummaryCheckpoints
                .SingleOrDefaultAsync(item => item.ProjectId == project.Id, cancellationToken);
            // 项目创建时间按 UTC 保存；活动记录与汇总水位沿用北京时间墙上时间。
            var summaryStart = checkpoint?.LastSuccessfulSummaryAt ?? AppTime.ToBeijingTime(project.CreatedAt);

            var activities = await _context.ProjectActivityRecords
                .AsNoTracking()
                .Where(item => item.ProjectId == project.Id
                    && item.OccurredAt > summaryStart
                    && item.OccurredAt <= summaryCutoff)
                .OrderBy(item => item.OccurredAt)
                .ThenBy(item => item.Id)
                .ToListAsync(cancellationToken);

            if (activities.Count == 0)
            {
                _logger.LogInformation(
                    "项目 {ProjectId} 自 {SummaryStart:yyyy-MM-dd HH:mm:ss} 起无变更，跳过 AI 和日报创建",
                    project.Id,
                    summaryStart);
                continue;
            }

            var taskIds = activities
                .Where(item => item.EntityType == ProjectActivityEntityType.Task && item.EntityId.HasValue)
                .Select(item => item.EntityId!.Value)
                .Distinct()
                .ToList();

            var tasks = await _context.ToDoTasks
                .AsNoTracking()
                .Where(item => item.ProjectId == project.Id && taskIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
            var activityNotes = await BuildActivityNotesAsync(activities, cancellationToken);

            var completedTasks = tasks
                .Where(item => !item.IsDeleted && item.Status == ToDo.Entities.TaskStatus.Completed)
                .Select(item => item.Title)
                .Distinct()
                .ToList();
            var uncompletedTasks = tasks
                .Where(item => !item.IsDeleted
                    && item.Status != ToDo.Entities.TaskStatus.Completed
                    && item.Status != ToDo.Entities.TaskStatus.Cancelled)
                .Select(item => item.Title)
                .Distinct()
                .ToList();

            AddNewTaskNamesWithoutIds(activities, completedTasks, uncompletedTasks);

            var aiResult = await _aiService.GenerateDailyReportAsync(new AIDailyReportInput
            {
                ReportDate = reportDate.Date,
                CompletedTasks = completedTasks,
                UncompletedTasks = uncompletedTasks,
                ActivityNotes = activityNotes,
                Problems = tasks
                    .Where(item => !item.IsDeleted && item.Status == ToDo.Entities.TaskStatus.PendingConfirmation)
                    .Select(item => $"待审核：{item.Title}")
                    .Distinct()
                    .ToList()
            });

            if (!aiResult.Success || string.IsNullOrWhiteSpace(aiResult.GeneratedReport))
            {
                var reason = string.IsNullOrWhiteSpace(aiResult.ErrorMessage) ? "AI 未返回日报内容" : aiResult.ErrorMessage;
                failures.Add($"{project.Name}：{reason}");
                _logger.LogWarning(
                    "项目 {ProjectId} 自动日报生成失败，汇总水位保持在 {SummaryStart:yyyy-MM-dd HH:mm:ss}：{Reason}",
                    project.Id,
                    summaryStart,
                    reason);
                continue;
            }

            var reporterId = job.UserId ?? project.LeaderUserId;
            var report = new DailyReport
            {
                ProjectId = project.Id,
                ReportType = 1,
                ReportDate = reportDate.Date,
                ReportTitle = $"{project.Name} 自动日报 {summaryCutoff:yyyy-MM-dd HH:mm}",
                ReportContent = aiResult.GeneratedReport,
                ReporterId = reporterId,
                IsSystemGenerated = true,
                CreatedAt = AppTime.Now,
                LastModifiedAt = AppTime.Now
            };
            _context.DailyReport.Add(report);

            if (checkpoint == null)
            {
                checkpoint = new ProjectSummaryCheckpoint
                {
                    ProjectId = project.Id,
                    Version = 1
                };
                _context.ProjectSummaryCheckpoints.Add(checkpoint);
            }
            else
            {
                checkpoint.Version++;
            }

            checkpoint.LastSuccessfulSummaryAt = summaryCutoff;
            checkpoint.LastDailyReport = report;
            checkpoint.UpdatedAt = AppTime.Now;

            // 日报和水位在同一次 SaveChanges 中提交：任一失败都不会前移水位。
            await _context.SaveChangesAsync(cancellationToken);

            try
            {
                await _eventBus.PublishAsync(
                    "daily-report.generated",
                    new
                    {
                        project.Id,
                        reporterId,
                        reportDate = reportDate.Date,
                        summaryStart,
                        summaryCutoff,
                        activityCount = activities.Count
                    },
                    "Project",
                    project.Id.ToString(),
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                // 日报已经成功入库，事件投递失败不回退汇总水位，事件总线自身会保留失败信息。
                _logger.LogError(ex, "项目 {ProjectId} 日报已生成，但事件发布失败", project.Id);
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"{failures.Count} 个项目生成失败：{string.Join("；", failures)}");
        }
    }

    private async Task<List<string>> BuildActivityNotesAsync(
        IReadOnlyCollection<ProjectActivityRecord> activities,
        CancellationToken cancellationToken)
    {
        var userIds = activities
            .Where(item => item.FieldName == "Assignee")
            .SelectMany(item => new[] { item.OldValue, item.NewValue })
            .Where(value => value?.StartsWith("human:", StringComparison.Ordinal) == true)
            .Select(value => int.TryParse(value!["human:".Length..], out var id) ? (int?)id : null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var userNames = await _context.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(
                user => user.Id,
                user => string.IsNullOrWhiteSpace(user.RealName) ? user.UserName ?? $"用户#{user.Id}" : user.RealName,
                cancellationToken);

        return activities.Select(item => FormatActivity(item, userNames)).ToList();
    }

    private static string FormatActivity(ProjectActivityRecord item, IReadOnlyDictionary<int, string> userNames)
    {
        var name = string.IsNullOrWhiteSpace(item.EntityName) ? "未命名" : item.EntityName;
        if (item.ChangeType == ProjectActivityChangeType.Created)
        {
            return item.EntityType switch
            {
                ProjectActivityEntityType.Task => $"创建任务「{name}」（初始状态：{FormatTaskStatus(item.NewValue)}）",
                ProjectActivityEntityType.MeetingMinutes => $"创建会议纪要「{name}」",
                ProjectActivityEntityType.ProjectDocument => $"上传项目资料「{name}」（分类：{FormatCategory(item.NewValue)}）",
                _ => $"创建项目内容「{name}」"
            };
        }

        if (item.ChangeType is ProjectActivityChangeType.Deleted or ProjectActivityChangeType.Restored)
        {
            var action = item.ChangeType == ProjectActivityChangeType.Deleted ? "删除" : "恢复";
            return $"{action}{FormatEntityType(item.EntityType)}「{name}」";
        }

        return (item.EntityType, item.FieldName) switch
        {
            (ProjectActivityEntityType.Task, "Status") => $"任务「{name}」状态：{FormatTaskStatus(item.OldValue)} → {FormatTaskStatus(item.NewValue)}",
            (ProjectActivityEntityType.Task, "Assignee") => $"任务「{name}」负责人：{FormatAssignee(item.OldValue, userNames)} → {FormatAssignee(item.NewValue, userNames)}",
            (ProjectActivityEntityType.Task, "Deadline") => $"任务「{name}」截止时间：{FormatDateTime(item.OldValue)} → {FormatDateTime(item.NewValue)}",
            (ProjectActivityEntityType.Task, "Progress") => $"任务「{name}」进度：{item.OldValue ?? "0"}% → {item.NewValue ?? "0"}%",
            (ProjectActivityEntityType.Task, "Title") => $"任务重命名：{item.OldValue ?? "未命名"} → {item.NewValue ?? name}",
            (ProjectActivityEntityType.Task, "Description") => $"更新任务「{name}」的描述",
            (ProjectActivityEntityType.MeetingMinutes, "Title") => $"会议纪要重命名：{item.OldValue ?? "未命名"} → {item.NewValue ?? name}",
            (ProjectActivityEntityType.MeetingMinutes, "MeetingDate") => $"会议纪要「{name}」会议时间：{FormatDateTime(item.OldValue)} → {FormatDateTime(item.NewValue)}",
            (ProjectActivityEntityType.MeetingMinutes, "Content") => $"更新会议纪要「{name}」的内容或原始记录",
            (ProjectActivityEntityType.ProjectDocument, "Category") => $"项目资料「{name}」分类：{FormatCategory(item.OldValue)} → {FormatCategory(item.NewValue)}",
            (ProjectActivityEntityType.ProjectDocument, "Content") => $"更新项目资料「{name}」的内容、说明或版本",
            (ProjectActivityEntityType.Project, "Status") => $"项目「{name}」状态：{item.OldValue ?? "未知"} → {item.NewValue ?? "未知"}",
            (ProjectActivityEntityType.Project, "Name") => $"项目重命名：{item.OldValue ?? "未命名"} → {item.NewValue ?? name}",
            (ProjectActivityEntityType.Project, "Content") => $"更新项目「{name}」的说明或目标",
            _ => $"更新{FormatEntityType(item.EntityType)}「{name}」"
        };
    }

    private static void AddNewTaskNamesWithoutIds(
        IEnumerable<ProjectActivityRecord> activities,
        ICollection<string> completedTasks,
        ICollection<string> uncompletedTasks)
    {
        foreach (var activity in activities.Where(item => item.EntityType == ProjectActivityEntityType.Task
                     && item.ChangeType == ProjectActivityChangeType.Created
                     && !item.EntityId.HasValue))
        {
            if (string.Equals(activity.NewValue, ToDo.Entities.TaskStatus.Completed.ToString(), StringComparison.Ordinal))
            {
                if (!completedTasks.Contains(activity.EntityName)) completedTasks.Add(activity.EntityName);
            }
            else if (!string.Equals(activity.NewValue, ToDo.Entities.TaskStatus.Cancelled.ToString(), StringComparison.Ordinal)
                     && !uncompletedTasks.Contains(activity.EntityName))
            {
                uncompletedTasks.Add(activity.EntityName);
            }
        }
    }

    private static string FormatTaskStatus(string? value)
    {
        return value switch
        {
            nameof(ToDo.Entities.TaskStatus.NotStarted) => "未开始",
            nameof(ToDo.Entities.TaskStatus.InProgress) => "进行中",
            nameof(ToDo.Entities.TaskStatus.Completed) => "已完成",
            nameof(ToDo.Entities.TaskStatus.Cancelled) => "已取消",
            nameof(ToDo.Entities.TaskStatus.PendingConfirmation) => "待审核",
            _ => string.IsNullOrWhiteSpace(value) ? "未知" : value
        };
    }

    private static string FormatAssignee(string? value, IReadOnlyDictionary<int, string> userNames)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "none") return "未指派";
        if (value.StartsWith("agent:", StringComparison.Ordinal))
        {
            var agentName = value["agent:".Length..];
            return string.IsNullOrWhiteSpace(agentName) ? "未指定数字员工" : $"数字员工 {agentName}";
        }

        if (value.StartsWith("human:", StringComparison.Ordinal)
            && int.TryParse(value["human:".Length..], out var userId))
        {
            return userNames.TryGetValue(userId, out var name) ? name : $"用户#{userId}";
        }

        return value;
    }

    private static string FormatCategory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "未分类";
        var parts = value.Split(':', 3);
        if (parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2])) return parts[2];
        return value;
    }

    private static string FormatDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "未设置";
        return DateTime.TryParse(value, out var result) ? result.ToString("yyyy-MM-dd HH:mm") : value;
    }

    private static string FormatEntityType(ProjectActivityEntityType entityType)
    {
        return entityType switch
        {
            ProjectActivityEntityType.Task => "任务",
            ProjectActivityEntityType.MeetingMinutes => "会议纪要",
            ProjectActivityEntityType.ProjectDocument => "项目资料",
            _ => "项目"
        };
    }

    private static DateTime GetNextRun(DateTime from, TimeSpan runAt)
    {
        var next = from.Date.Add(runAt);
        return next <= from ? next.AddDays(1) : next;
    }
}

public class TeamReportCheckpoint
{
    public int ProjectId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public DateTime LastSuccessfulSummaryAt { get; set; }
    public int LastTeamReportId { get; set; }
    public string LastTeamReportTitle { get; set; } = string.Empty;
}

public class ScheduledTaskHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduledTaskHostedService> _logger;

    public ScheduledTaskHostedService(IServiceScopeFactory scopeFactory, ILogger<ScheduledTaskHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ScheduledJobService>().RunDueJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "定时任务调度失败");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}

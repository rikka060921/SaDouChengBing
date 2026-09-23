using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToDo.Context;
using ToDo.Domain.Options;
using ToDo.Entities;

namespace ToDo.Domain;

public sealed record DataRetentionPreview(
    DateTime ArchiveSessionsBefore,
    DateTime DeleteNotificationsBefore,
    int SessionsToArchive,
    int NotificationsToDelete,
    bool AutomaticExecutionEnabled);

public sealed record DataRetentionResult(
    bool Executed,
    int ArchivedSessionCount,
    int DeletedNotificationCount,
    string Message,
    long? RunId);

/// <summary>
/// 执行最小破坏的数据生命周期策略：旧 Session 仅归档，验收凭证、工具调用和审批审计永久保留；
/// 只有已经读取且超过保留期的通知会被物理清理。
/// </summary>
public sealed class DataRetentionService
{
    private const string LeaseKey = "data-retention:daily";
    private readonly ApplicationDbContext _context;
    private readonly DistributedLeaseService _leases;
    private readonly DataRetentionOptions _options;

    public DataRetentionService(
        ApplicationDbContext context,
        DistributedLeaseService leases,
        IOptions<DataRetentionOptions> options)
    {
        _context = context;
        _leases = leases;
        _options = options.Value;
    }

    public async Task<DataRetentionPreview> PreviewAsync(
        DateTime? now = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveNow = now ?? AppTime.Now;
        var archiveBefore = effectiveNow.AddDays(-ArchiveDays);
        var notificationsBefore = effectiveNow.AddDays(-NotificationDays);
        var terminalStatuses = new[]
        {
            AiSessionStatus.Succeeded,
            AiSessionStatus.Failed,
            AiSessionStatus.Cancelled
        };
        var sessions = await _context.AiSessions.AsNoTracking()
            .CountAsync(item => item.ArchivedAt == null
                && terminalStatuses.Contains(item.Status)
                && item.LastActivityAt < archiveBefore, cancellationToken);
        var notifications = await _context.UserNotifications.AsNoTracking()
            .CountAsync(item => item.IsRead
                && (item.ReadAt ?? item.CreatedAt) < notificationsBefore, cancellationToken);
        return new DataRetentionPreview(
            archiveBefore,
            notificationsBefore,
            sessions,
            notifications,
            _options.Enabled);
    }

    public async Task<DataRetentionResult> RunAsync(
        int? triggeredByUserId,
        string trigger,
        DateTime? now = null,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await _leases.TryAcquireAsync(LeaseKey, TimeSpan.FromMinutes(30), cancellationToken);
        if (lease == null)
            return new DataRetentionResult(false, 0, 0, "已有留存任务正在执行", null);

        var effectiveNow = now ?? AppTime.Now;
        var archiveBefore = effectiveNow.AddDays(-ArchiveDays);
        var notificationsBefore = effectiveNow.AddDays(-NotificationDays);
        var run = new DataRetentionRun
        {
            TriggeredByUserId = triggeredByUserId,
            Trigger = NormalizeTrigger(trigger),
            ArchiveSessionsBefore = archiveBefore,
            DeleteNotificationsBefore = notificationsBefore,
            StartedAt = effectiveNow
        };
        _context.DataRetentionRuns.Add(run);
        await _context.SaveChangesAsync(cancellationToken);
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var terminalStatuses = new[]
            {
                AiSessionStatus.Succeeded,
                AiSessionStatus.Failed,
                AiSessionStatus.Cancelled
            };
            var sessionIds = await _context.AiSessions.AsNoTracking()
                .Where(item => item.ArchivedAt == null
                    && terminalStatuses.Contains(item.Status)
                    && item.LastActivityAt < archiveBefore)
                .OrderBy(item => item.Id)
                .Select(item => item.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (sessionIds.Count > 0)
            {
                run.ArchivedSessionCount = await _context.AiSessions
                    .Where(item => sessionIds.Contains(item.Id) && item.ArchivedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ArchivedAt, effectiveNow), cancellationToken);
            }

            var notificationIds = await _context.UserNotifications.AsNoTracking()
                .Where(item => item.IsRead && (item.ReadAt ?? item.CreatedAt) < notificationsBefore)
                .OrderBy(item => item.Id)
                .Select(item => item.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (notificationIds.Count > 0)
            {
                run.DeletedNotificationCount = await _context.UserNotifications
                    .Where(item => notificationIds.Contains(item.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            run.Status = DataRetentionRunStatus.Completed;
            run.CompletedAt = AppTime.Now;
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new DataRetentionResult(
                true,
                run.ArchivedSessionCount,
                run.DeletedNotificationCount,
                "留存策略执行完成",
                run.Id);
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _context.ChangeTracker.Clear();
            var failedRun = await _context.DataRetentionRuns.FirstAsync(item => item.Id == run.Id, CancellationToken.None);
            failedRun.Status = DataRetentionRunStatus.Failed;
            failedRun.ErrorMessage = exception.Message.Length > 2000 ? exception.Message[..2000] : exception.Message;
            failedRun.CompletedAt = AppTime.Now;
            await _context.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public Task<List<DataRetentionRun>> GetRecentRunsAsync(int take = 20, CancellationToken cancellationToken = default)
        => _context.DataRetentionRuns.AsNoTracking()
            .Include(item => item.TriggeredByUser)
            .OrderByDescending(item => item.StartedAt)
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(cancellationToken);

    private int ArchiveDays => Math.Clamp(_options.ArchiveAiSessionsAfterDays, 30, 3650);
    private int NotificationDays => Math.Clamp(_options.DeleteReadNotificationsAfterDays, 7, 3650);
    private int BatchSize => Math.Clamp(_options.BatchSize, 10, 5000);

    private static string NormalizeTrigger(string trigger)
    {
        var value = string.IsNullOrWhiteSpace(trigger) ? "manual" : trigger.Trim().ToLowerInvariant();
        return value.Length <= 30 ? value : value[..30];
    }
}

public sealed class DataRetentionHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DataRetentionOptions _options;
    private readonly ILogger<DataRetentionHostedService> _logger;

    public DataRetentionHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<DataRetentionOptions> options,
        ILogger<DataRetentionHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("自动数据留存任务未启用，可在数据治理页面手动预览和执行");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Clamp(_options.RunIntervalHours, 1, 168));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<DataRetentionService>()
                    .RunAsync(null, "scheduled", cancellationToken: stoppingToken);
                _logger.LogInformation(
                    "数据留存任务：{Message}，归档 Session {SessionCount}，清理通知 {NotificationCount}",
                    result.Message,
                    result.ArchivedSessionCount,
                    result.DeletedNotificationCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "自动数据留存任务执行失败");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public sealed record AutomationHealthSnapshot(
    bool DatabaseAvailable,
    int PendingAssignmentJobs,
    int PendingManualJobs,
    int StaleJobs,
    int PendingApprovals,
    int PendingDeliveries,
    DateTime CheckedAt)
{
    public string Status => !DatabaseAvailable ? "unhealthy" : StaleJobs > 0 || PendingAssignmentJobs + PendingManualJobs > 1000 ? "degraded" : "healthy";
}

public sealed class AutomationHealthService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<AutomationHealthService> _logger;
    public AutomationHealthService(ApplicationDbContext context, ILogger<AutomationHealthService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<AutomationHealthSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _context.Database.CanConnectAsync(cancellationToken))
                return Unavailable();
            var now = AppTime.Now;
            var staleCutoff = now.Subtract(AgentWorkQueueService.StaleWorkItemTimeout);
            var pendingAssignment = await _context.AgentWorkItems.AsNoTracking()
                .CountAsync(item => item.Status == AgentWorkItemStatus.Pending || item.Status == AgentWorkItemStatus.Retrying, cancellationToken);
            var pendingManual = await _context.AgentRunJobs.AsNoTracking()
                .CountAsync(item => item.Status == AgentRunJobStatus.Pending || item.Status == AgentRunJobStatus.Retrying, cancellationToken);
            var staleAssignment = await _context.AgentWorkItems.AsNoTracking()
                .CountAsync(item => item.Status == AgentWorkItemStatus.Running && item.LockedAt < staleCutoff, cancellationToken);
            var staleManual = await _context.AgentRunJobs.AsNoTracking()
                .CountAsync(item => item.Status == AgentRunJobStatus.Running && item.LockedAt < staleCutoff, cancellationToken);
            var pendingApprovals = await _context.ApprovalRequests.AsNoTracking()
                .CountAsync(item => item.Status == ApprovalRequestStatus.Pending, cancellationToken);
            var pendingDeliveries = await _context.AgentDeliveryReceipts.AsNoTracking()
                .CountAsync(item => item.AcceptanceStatus == AgentDeliveryAcceptanceStatus.PendingReview, cancellationToken);
            return new AutomationHealthSnapshot(true, pendingAssignment, pendingManual, staleAssignment + staleManual, pendingApprovals, pendingDeliveries, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "读取 Agent 自动化健康状态失败");
            return Unavailable();
        }
    }

    private static AutomationHealthSnapshot Unavailable()
        => new(false, 0, 0, 0, 0, 0, AppTime.Now);
}

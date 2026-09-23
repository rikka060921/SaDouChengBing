using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

/// <summary>用户手工运行和继续对话的持久化后台队列。</summary>
public sealed class AgentRunQueueService
{
    private readonly ApplicationDbContext _context;
    private readonly IAgentRegistry _registry;
    private readonly AiSessionService _sessions;
    private readonly AgentExecutionService _execution;
    private readonly IEventBus _eventBus;
    private readonly ILogger<AgentRunQueueService> _logger;
    private readonly AgentQueueSignal? _queueSignal;
    private readonly AgentAcceptanceService? _acceptanceService;
    private readonly AgentTelemetry? _telemetry;

    public AgentRunQueueService(
        ApplicationDbContext context,
        IAgentRegistry registry,
        AiSessionService sessions,
        AgentExecutionService execution,
        IEventBus eventBus,
        ILogger<AgentRunQueueService> logger,
        AgentQueueSignal? queueSignal = null,
        AgentAcceptanceService? acceptanceService = null,
        AgentTelemetry? telemetry = null)
    {
        _context = context;
        _registry = registry;
        _sessions = sessions;
        _execution = execution;
        _eventBus = eventBus;
        _logger = logger;
        _queueSignal = queueSignal;
        _acceptanceService = acceptanceService;
        _telemetry = telemetry;
    }

    public Task<AgentRunJob> EnqueueNewAsync(
        string agentKey,
        string prompt,
        ApplicationUser user,
        int? projectId,
        int? taskId,
        CancellationToken cancellationToken = default,
        long? deliveryReceiptId = null)
        => EnqueueNewCoreAsync(agentKey, prompt, user, projectId, taskId, cancellationToken, deliveryReceiptId);

    internal Task<AgentRunJob> EnqueueTaskAssistanceAsync(int taskId, int projectId, string purpose,
        ApplicationUser user, string sessionKey, CancellationToken cancellationToken)
        => EnqueueNewCoreAsync(TaskAssistanceService.AgentKey, TaskAssistanceService.BuildPrompt(purpose),
            user, projectId, taskId, cancellationToken, null, sessionKey, purpose);

    private async Task<AgentRunJob> EnqueueNewCoreAsync(string agentKey, string prompt, ApplicationUser user,
        int? projectId, int? taskId, CancellationToken cancellationToken, long? deliveryReceiptId,
        string? sessionKey = null, string? assistancePurpose = null)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("Agent 指令不能为空");
        if (prompt.Length > 8000) throw new InvalidOperationException("Agent 指令不能超过 8000 个字符");
        await EnsureActiveUserAsync(user.Id, cancellationToken);
        var routingKey = $"manual:{user.Id}:{projectId}:{taskId}:{prompt.Trim()}";
        var definition = await _registry.GetForExecutionAsync(agentKey, routingKey: routingKey, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Agent 不存在或已停用");
        (projectId, taskId) = await ValidateScopeAsync(definition, user, projectId, taskId, cancellationToken);
        await ValidateDeliveryReceiptAsync(deliveryReceiptId, projectId, taskId, cancellationToken);

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        // SessionKey 尚无唯一索引。利用现有租约主键在同一事务中锁定请求，避免多实例重复入队。
        // 事务持有租约行写锁直到 Session 与 Job 一起提交，不能仅靠进程内锁或过期时间。
        await using var requestLease = sessionKey == null ? null : await new DistributedLeaseService(_context)
            .TryAcquireAsync(sessionKey, TimeSpan.FromMinutes(5), cancellationToken);
        if (sessionKey != null)
        {
            if (requestLease == null) throw new InvalidOperationException("这次 AI 协助正在提交，请稍后刷新查看结果");
            if (!taskId.HasValue || !await TaskAssistanceService.CanAccessAsync(_context, taskId.Value, user.Id, true, cancellationToken))
                throw new UnauthorizedAccessException("当前任务不可请求协助，或你已不具备处理权限");
            var existing = await _context.AgentRunJobs.Include(item => item.AiSession)
                .Where(item => item.RequestedByUserId == user.Id && item.TaskId == taskId
                    && item.AiSession != null && item.AiSession.UserId == user.Id && item.AiSession.SessionKey == sessionKey)
                .OrderBy(item => item.Id).FirstOrDefaultAsync(cancellationToken);
            if (existing != null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }
        }
        var session = await _sessions.StartAsync(
            definition.AgentKey,
            prompt.Trim(),
            user.Id,
            projectId,
            taskId,
            definition.ModelName,
            definition.Version,
            sessionKey,
            assistancePurpose == null ? null : AgentSessionExecutionPolicy.Metadata(assistancePurpose));
        session.ContextDeliveryReceiptId = deliveryReceiptId;
        var job = new AgentRunJob
        {
            AiSessionId = session.Id,
            AgentDefinitionId = definition.Id,
            RequestedByUserId = user.Id,
            ProjectId = projectId,
            TaskId = taskId,
            DeliveryReceiptId = deliveryReceiptId,
            Prompt = prompt.Trim(),
            Status = AgentRunJobStatus.Pending,
            NextRunAt = AppTime.Now
        };
        _context.AgentRunJobs.Add(job);
        await _context.SaveChangesAsync(cancellationToken);
        if (deliveryReceiptId.HasValue
            && string.Equals(definition.AgentKey, "delivery-acceptance", StringComparison.OrdinalIgnoreCase))
        {
            _context.AgentAcceptanceRecommendations.Add(new AgentAcceptanceRecommendation
            {
                DeliveryReceiptId = deliveryReceiptId.Value,
                AiSessionId = session.Id,
                AgentRunJobId = job.Id,
                RequestedByUserId = user.Id,
                Status = AgentAcceptanceRecommendationStatus.Pending,
                CreatedAt = AppTime.Now
            });
        }
        if (assistancePurpose == null) session.MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            runJobId = job.Id,
            source = "manual-queue",
            deliveryReceiptId
        });
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _queueSignal?.Notify();
        _telemetry?.RecordQueued("manual");
        await TryPublishQueuedAsync(session.Id, job.Id, definition.AgentKey, false, cancellationToken);
        return job;
    }

    public async Task<AgentRunJob> EnqueueContinuationAsync(
        int sessionId,
        string prompt,
        ApplicationUser user,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("继续指令不能为空");
        if (prompt.Length > 8000) throw new InvalidOperationException("继续指令不能超过 8000 个字符");
        await EnsureActiveUserAsync(user.Id, cancellationToken);
        var session = await _sessions.GetDetailsAsync(sessionId, user)
            ?? throw new UnauthorizedAccessException("Session 不存在或无权访问");
        if (AgentSessionExecutionPolicy.IsTaskAssistance(session)
            && !await TaskAssistanceService.CanAccessSessionAsync(_context, session, user.Id, true, cancellationToken))
            throw new UnauthorizedAccessException("当前任务已结束或不可继续请求协助");
        if (session.Status is AiSessionStatus.Succeeded or AiSessionStatus.Cancelled)
            throw new InvalidOperationException("该 Session 已结束");
        var active = await _context.AgentRunJobs.AsNoTracking().AnyAsync(item => item.AiSessionId == sessionId
            && (item.Status == AgentRunJobStatus.Pending
                || item.Status == AgentRunJobStatus.Running
                || item.Status == AgentRunJobStatus.Retrying
                || item.Status == AgentRunJobStatus.WaitingApproval), cancellationToken);
        if (active || session.Status == AiSessionStatus.Running)
            throw new InvalidOperationException("该 Session 已有一条指令正在排队或执行");
        var definition = await _registry.GetForExecutionAsync(session.AgentKey, session.AgentVersion, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Agent 不存在或已停用");

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        await _sessions.BeginTurnAsync(session, prompt.Trim());
        var job = new AgentRunJob
        {
            AiSessionId = session.Id,
            AgentDefinitionId = definition.Id,
            RequestedByUserId = user.Id,
            ProjectId = session.ProjectId,
            TaskId = session.TaskId,
            DeliveryReceiptId = session.ContextDeliveryReceiptId,
            Prompt = prompt.Trim(),
            Status = AgentRunJobStatus.Pending,
            NextRunAt = AppTime.Now
        };
        _context.AgentRunJobs.Add(job);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _queueSignal?.Notify();
        _telemetry?.RecordQueued("manual-continuation");
        await TryPublishQueuedAsync(session.Id, job.Id, definition.AgentKey, true, cancellationToken);
        return job;
    }

    public Task<AgentRunJob?> GetCurrentAsync(int sessionId, CancellationToken cancellationToken = default)
        => _context.AgentRunJobs.AsNoTracking()
            .Include(item => item.WaitingApprovalRequest)
            .Where(item => item.AiSessionId == sessionId)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task CancelAsync(long jobId, ApplicationUser user, CancellationToken cancellationToken = default)
    {
        var job = await _context.AgentRunJobs.Include(item => item.AiSession)
            .FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException("运行任务不存在");
        if (job.AiSession != null && AgentSessionExecutionPolicy.IsTaskAssistance(job.AiSession)
            && !await TaskAssistanceService.CanAccessSessionAsync(_context, job.AiSession, user.Id, false, cancellationToken))
            throw new UnauthorizedAccessException("无权取消该 AI 协助");
        if (user.Role != UserRole.systemAdmin && job.RequestedByUserId != user.Id)
            throw new UnauthorizedAccessException("无权取消该运行任务");
        if (job.Status is not (AgentRunJobStatus.Pending or AgentRunJobStatus.Retrying))
            throw new InvalidOperationException("只有尚未开始或等待重试的运行任务可以取消");
        job.Status = AgentRunJobStatus.Cancelled;
        job.CompletedAt = AppTime.Now;
        job.UpdatedAt = AppTime.Now;
        job.ErrorMessage = "用户取消";
        if (job.AiSession != null)
        {
            job.AiSession.Status = AiSessionStatus.Cancelled;
            job.AiSession.CompletedAt = AppTime.Now;
            job.AiSession.LastActivityAt = AppTime.Now;
            job.AiSession.ConcurrencyVersion++;
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task ResumeResolvedApprovalsAsync(CancellationToken cancellationToken = default)
    {
        var jobs = await _context.AgentRunJobs
            .Include(item => item.AiSession)
            .Where(item => item.Status == AgentRunJobStatus.WaitingApproval)
            .OrderBy(item => item.UpdatedAt).Take(20).ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            var waiting = await _context.AgentToolCalls.AsNoTracking().AnyAsync(call => call.AiSessionId == job.AiSessionId
                && call.Status == AgentToolCallStatus.PendingApproval, cancellationToken);
            if (waiting) continue;
            if (job.AiSession == null) { job.Status = AgentRunJobStatus.Failed; job.ErrorMessage = "Session 不存在"; continue; }
            await _sessions.BeginTurnAsync(job.AiSession, "系统正在继续已完成审核的操作。请读取工具结果，给出结论；如仍需操作，只调用必要工具，不要重复调用。");
            job.Status = AgentRunJobStatus.Pending;
            job.WaitingApprovalRequestId = null;
            job.NextRunAt = AppTime.Now;
            job.UpdatedAt = AppTime.Now;
        }
        await _context.SaveChangesAsync(cancellationToken);
        if (jobs.Any(item => item.Status == AgentRunJobStatus.Pending)) _queueSignal?.Notify();
    }

    public async Task RecoverStaleAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = AppTime.Now.AddMinutes(-15);
        var jobs = await _context.AgentRunJobs.Where(item => item.Status == AgentRunJobStatus.Running && item.LockedAt < cutoff)
            .Take(20).ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            job.Status = job.AttemptCount >= job.MaxAttempts ? AgentRunJobStatus.Failed : AgentRunJobStatus.Retrying;
            job.ErrorMessage = job.Status == AgentRunJobStatus.Failed ? "后台执行超时且已达到最大尝试次数" : "后台执行中断，已恢复重试";
            job.LockedAt = null;
            job.NextRunAt = AppTime.Now.AddMinutes(1);
            job.UpdatedAt = AppTime.Now;
            if (job.Status == AgentRunJobStatus.Failed) job.CompletedAt = AppTime.Now;
        }
        if (jobs.Count > 0) await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<long?> ClaimNextAsync(CancellationToken cancellationToken = default)
    {
        var now = AppTime.Now;
        var candidate = await _context.AgentRunJobs.AsNoTracking()
            .Where(item => (item.Status == AgentRunJobStatus.Pending
                    || (item.Status == AgentRunJobStatus.Retrying && item.AttemptCount < item.MaxAttempts))
                && item.NextRunAt <= now)
            .OrderBy(item => item.NextRunAt).ThenBy(item => item.Id)
            .Select(item => new { item.Id, item.Status, item.AttemptCount })
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate == null) return null;

        // Pending 既表示首次运行，也表示一次成功工具调用后的下一步骤。
        // 只有 Retrying 才消耗新的重试次数，避免多步骤 Agent 在达到 MaxAttempts 后永久卡在等待执行。
        var nextAttemptCount = candidate.Status == AgentRunJobStatus.Retrying
            ? candidate.AttemptCount + 1
            : Math.Max(candidate.AttemptCount, 1);
        var affected = await _context.AgentRunJobs.Where(item => item.Id == candidate.Id
                && item.Status == candidate.Status
                && item.AttemptCount == candidate.AttemptCount
                && item.NextRunAt <= now
                && (item.Status == AgentRunJobStatus.Pending || item.AttemptCount < item.MaxAttempts))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AgentRunJobStatus.Running)
                .SetProperty(item => item.AttemptCount, nextAttemptCount)
                .SetProperty(item => item.LockedAt, now)
                .SetProperty(item => item.StartedAt, item => item.StartedAt ?? now)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (affected != 1) return null;
        var tracked = _context.ChangeTracker.Entries<AgentRunJob>().FirstOrDefault(entry => entry.Entity.Id == candidate.Id);
        if (tracked != null) await tracked.ReloadAsync(cancellationToken);
        return candidate.Id;
    }

    public async Task ProcessAsync(long jobId, CancellationToken cancellationToken = default)
    {
        var job = await _context.AgentRunJobs
            .Include(item => item.AiSession)
            .Include(item => item.AgentDefinition)
            .Include(item => item.RequestedByUser)
            .FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job == null || job.Status != AgentRunJobStatus.Running) return;
        using var activity = _telemetry?.StartConsumer("agent.manual.process", "manual", job.Id, job.ProjectId, job.TaskId);
        var stopwatch = Stopwatch.StartNew();
        var stepSucceeded = false;
        try
        {
            if (job.AiSession == null || job.AgentDefinition?.IsEnabled != true || job.RequestedByUser?.Status != UserStatus.Active)
                throw new InvalidOperationException("Session、Agent 或发起用户已失效");
            if (job.AiSession.Status == AiSessionStatus.Failed)
                await _sessions.BeginTurnAsync(job.AiSession, "系统重试上一轮失败的请求。请基于已有对话继续，不要重复已成功操作。");
            var previousCallId = await _context.AgentToolCalls.AsNoTracking().Where(call => call.AiSessionId == job.AiSessionId)
                .MaxAsync(call => (int?)call.Id, cancellationToken) ?? 0;
            var (session, response) = await _execution.ExecutePendingTurnAsync(job.AiSessionId, job.RequestedByUser, cancellationToken);
            stepSucceeded = true;
            job.StepCount++;
            job.ResultSummary = response;
            job.ErrorMessage = string.Empty;
            job.LockedAt = null;
            job.UpdatedAt = AppTime.Now;
            var calls = await _context.AgentToolCalls.AsNoTracking().Where(call => call.AiSessionId == job.AiSessionId && call.Id > previousCallId)
                .OrderBy(call => call.Id).ToListAsync(cancellationToken);
            var pending = calls.FirstOrDefault(call => call.Status == AgentToolCallStatus.PendingApproval);
            if (pending != null)
            {
                job.Status = AgentRunJobStatus.WaitingApproval;
                job.WaitingApprovalRequestId = pending.ApprovalRequestId;
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }
            // 上下文读取是系统审计，不是需要 Agent 再处理一轮的工具结果。
            var hasContinuationToolCall = calls.Any(call =>
                !string.Equals(call.ToolName, AgentContextService.ContextReadToolName, StringComparison.OrdinalIgnoreCase));
            if (hasContinuationToolCall && job.StepCount < job.MaxSteps)
            {
                await _sessions.BeginTurnAsync(session, "系统继续处理上一轮工具结果。目标完成则给出结论；否则只调用必要工具，禁止重复调用。");
                job.Status = AgentRunJobStatus.Pending;
                job.NextRunAt = AppTime.Now.AddSeconds(1);
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }
            job.Status = AgentRunJobStatus.Completed;
            job.CompletedAt = AppTime.Now;
            if (_acceptanceService != null)
                await _acceptanceService.CompleteAsync(job.AiSessionId, response, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await MarkInterruptedAsync(jobId, CancellationToken.None);
            }
            catch (Exception recoveryException)
            {
                _logger.LogError(recoveryException, "手工 Agent 运行任务 {JobId} 在服务停止时释放执行锁失败", jobId);
            }
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await MarkFailedOrRetryAsync(jobId, ex, cancellationToken);
        }
        finally
        {
            stopwatch.Stop();
            _telemetry?.RecordExecution(
                "manual",
                stepSucceeded,
                job.AttemptCount == 1 && job.StepCount <= 1
                    ? (job.StartedAt ?? AppTime.Now).Subtract(job.CreatedAt).TotalMilliseconds
                    : null,
                stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private async Task MarkFailedOrRetryAsync(long jobId, Exception exception, CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        var job = await _context.AgentRunJobs.Include(item => item.AiSession).FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job == null) return;
        var exhausted = job.AttemptCount >= job.MaxAttempts;
        job.Status = exhausted ? AgentRunJobStatus.Failed : AgentRunJobStatus.Retrying;
        job.ErrorMessage = exception.Message.Length > 2000 ? exception.Message[..2000] : exception.Message;
        job.LockedAt = null;
        job.UpdatedAt = AppTime.Now;
        job.NextRunAt = AppTime.Now.AddMinutes(job.AttemptCount <= 1 ? 1 : job.AttemptCount == 2 ? 5 : 15);
        if (exhausted) job.CompletedAt = AppTime.Now;
        if (exhausted && _acceptanceService != null)
            await _acceptanceService.FailAsync(job.AiSessionId, job.ErrorMessage, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogWarning(exception, "手工 Agent 运行任务 {JobId} 第 {Attempt}/{MaxAttempts} 次失败", job.Id, job.AttemptCount, job.MaxAttempts);
    }

    private async Task MarkInterruptedAsync(long jobId, CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        var job = await _context.AgentRunJobs.FirstOrDefaultAsync(
            item => item.Id == jobId && item.Status == AgentRunJobStatus.Running,
            cancellationToken);
        if (job == null) return;

        // 宿主停止不算一次业务失败；以原尝试次数恢复，避免达到上限后卡在不可领取的 Retrying。
        // 保留 Session 和工具记录，真正执行失败仍由 MarkFailedOrRetryAsync 按原上限终止。
        job.Status = AgentRunJobStatus.Pending;
        job.ErrorMessage = "Agent 服务停止，已释放执行锁并等待自动恢复";
        job.LockedAt = null;
        job.NextRunAt = AppTime.Now;
        job.UpdatedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<(int? ProjectId, int? TaskId)> ValidateScopeAsync(
        AgentDefinition definition, ApplicationUser user, int? projectId, int? taskId, CancellationToken cancellationToken)
    {
        if (taskId.HasValue)
        {
            var taskProjectId = await _context.ToDoTasks.AsNoTracking().Where(item => item.Id == taskId && !item.IsDeleted)
                .Select(item => (int?)item.ProjectId).FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException("任务不存在或已删除");
            if (projectId.HasValue && projectId != taskProjectId) throw new InvalidOperationException("任务不属于所选项目");
            projectId = taskProjectId;
        }
        if (definition.RequiresTask && !taskId.HasValue) throw new InvalidOperationException("该 Agent 必须选择任务");
        if (definition.RequiresProject && !projectId.HasValue) throw new InvalidOperationException("该 Agent 必须选择项目");
        if (projectId.HasValue && user.Role != UserRole.systemAdmin)
        {
            var allowed = await _context.Project.AsNoTracking().AnyAsync(project => project.Id == projectId && !project.IsDeleted
                && (project.LeaderUserId == user.Id || _context.ProjectUsers.Any(member => member.ProjectId == project.Id && member.UserId == user.Id)), cancellationToken);
            if (!allowed) throw new UnauthorizedAccessException("你无权访问所选项目");
        }
        return (projectId, taskId);
    }

    private async Task EnsureActiveUserAsync(int userId, CancellationToken cancellationToken)
    {
        if (!await _context.Users.AsNoTracking().AnyAsync(item => item.Id == userId && item.Status == UserStatus.Active && !item.IsDeleted, cancellationToken))
            throw new UnauthorizedAccessException("当前用户已停用或不存在");
    }

    private async Task ValidateDeliveryReceiptAsync(
        long? deliveryReceiptId,
        int? projectId,
        int? taskId,
        CancellationToken cancellationToken)
    {
        if (!deliveryReceiptId.HasValue) return;
        if (!taskId.HasValue) throw new InvalidOperationException("交付凭证审查必须关联任务");
        var receipt = await _context.AgentDeliveryReceipts.AsNoTracking()
            .Where(item => item.Id == deliveryReceiptId.Value)
            .Select(item => new { item.TaskId, item.ProjectId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("指定的交付凭证不存在");
        if (receipt.TaskId != taskId.Value || (projectId.HasValue && receipt.ProjectId != projectId.Value))
            throw new InvalidOperationException("交付凭证不属于当前任务或项目");
    }

    private async Task TryPublishQueuedAsync(int sessionId, long jobId, string agentKey, bool continuation, CancellationToken cancellationToken)
    {
        try
        {
            await _eventBus.PublishAsync("ai.session.queued", new
            {
                sessionId,
                jobId,
                agentKey,
                continuation
            }, "AiSession", sessionId.ToString(), cancellationToken);
        }
        catch (Exception ex)
        {
            // 运行任务已经持久化。事件通知失败不能让调用方误以为入队失败并重复提交。
            _logger.LogWarning(ex, "Agent 运行任务 {JobId} 已入队，但 queued 事件发布失败", jobId);
        }
    }
}

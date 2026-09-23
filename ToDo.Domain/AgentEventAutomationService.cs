using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public sealed class AgentEventSubscriptionService
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "ai.session.",
        "agent.event.",
        "agent.tool.",
        "agent.work-item."
    ];

    private readonly ApplicationDbContext _context;

    public AgentEventSubscriptionService(ApplicationDbContext context) => _context = context;

    public async Task<AgentEventSubscription> SaveAsync(
        AgentEventSubscription input,
        int operatedByUserId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSystemAdminAsync(operatedByUserId, cancellationToken);
        NormalizeAndValidate(input);

        var agentExists = await _context.AgentDefinitions.AsNoTracking()
            .AnyAsync(item => item.Id == input.AgentDefinitionId && item.IsEnabled, cancellationToken);
        if (!agentExists) throw new InvalidOperationException("所选 Agent 不存在或已停用");
        if (input.ProjectId.HasValue)
        {
            var projectExists = await _context.Project.AsNoTracking()
                .AnyAsync(item => item.Id == input.ProjectId && !item.IsDeleted, cancellationToken);
            if (!projectExists) throw new InvalidOperationException("所选项目不存在或已删除");
        }

        var duplicate = await _context.AgentEventSubscriptions.AsNoTracking()
            .AnyAsync(item => item.Id != input.Id
                && item.EventType == input.EventType
                && item.AgentDefinitionId == input.AgentDefinitionId
                && item.ProjectId == input.ProjectId, cancellationToken);
        if (duplicate) throw new InvalidOperationException("相同事件、Agent 和项目范围的订阅规则已经存在");

        AgentEventSubscription entity;
        if (input.Id > 0)
        {
            entity = await _context.AgentEventSubscriptions
                .FirstOrDefaultAsync(item => item.Id == input.Id, cancellationToken)
                ?? throw new InvalidOperationException("订阅规则不存在");
            entity.Name = input.Name;
            entity.EventType = input.EventType;
            entity.AgentDefinitionId = input.AgentDefinitionId;
            entity.ProjectId = input.ProjectId;
            entity.PromptTemplate = input.PromptTemplate;
            entity.CooldownSeconds = input.CooldownSeconds;
            entity.MaxAttempts = input.MaxAttempts;
            entity.MaxSteps = input.MaxSteps;
            entity.DailyExecutionLimit = input.DailyExecutionLimit;
            entity.MaxTokenBudget = input.MaxTokenBudget;
            entity.ConditionField = input.ConditionField;
            entity.ConditionOperator = input.ConditionOperator;
            entity.ConditionValue = input.ConditionValue;
            entity.IsEnabled = input.IsEnabled;
            entity.UpdatedAt = AppTime.Now;
        }
        else
        {
            entity = new AgentEventSubscription
            {
                Name = input.Name,
                EventType = input.EventType,
                AgentDefinitionId = input.AgentDefinitionId,
                ProjectId = input.ProjectId,
                CreatedByUserId = operatedByUserId,
                PromptTemplate = input.PromptTemplate,
                CooldownSeconds = input.CooldownSeconds,
                MaxAttempts = input.MaxAttempts,
                MaxSteps = input.MaxSteps,
                DailyExecutionLimit = input.DailyExecutionLimit,
                MaxTokenBudget = input.MaxTokenBudget,
                ConditionField = input.ConditionField,
                ConditionOperator = input.ConditionOperator,
                ConditionValue = input.ConditionValue,
                IsEnabled = input.IsEnabled,
                CreatedAt = AppTime.Now,
                UpdatedAt = AppTime.Now
            };
            _context.AgentEventSubscriptions.Add(entity);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task ToggleAsync(int id, int operatedByUserId, CancellationToken cancellationToken = default)
    {
        await EnsureSystemAdminAsync(operatedByUserId, cancellationToken);
        var rule = await _context.AgentEventSubscriptions.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("订阅规则不存在");
        rule.IsEnabled = !rule.IsEnabled;
        rule.UpdatedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public static bool IsEventTypeAllowed(string eventType)
    {
        var normalized = (eventType ?? string.Empty).Trim().ToLowerInvariant();
        return Regex.IsMatch(normalized, "^[a-z0-9][a-z0-9._-]{2,119}$")
            && ForbiddenPrefixes.All(prefix => !normalized.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static void NormalizeAndValidate(AgentEventSubscription input)
    {
        input.Name = (input.Name ?? string.Empty).Trim();
        input.EventType = (input.EventType ?? string.Empty).Trim().ToLowerInvariant();
        input.PromptTemplate = (input.PromptTemplate ?? string.Empty).Trim();
        input.ConditionField = (input.ConditionField ?? string.Empty).Trim();
        input.ConditionValue = (input.ConditionValue ?? string.Empty).Trim();
        if (input.Name.Length is < 2 or > 120) throw new InvalidOperationException("规则名称长度必须为 2 到 120 个字符");
        if (!IsEventTypeAllowed(input.EventType))
            throw new InvalidOperationException("事件类型格式不正确，或属于会造成 Agent 自触发循环的保留事件");
        if (input.PromptTemplate.Length is < 5 or > 12000)
            throw new InvalidOperationException("提示词模板长度必须为 5 到 12000 个字符");
        if (input.CooldownSeconds is < 0 or > 86400) throw new InvalidOperationException("冷却时间必须在 0 到 86400 秒之间");
        if (input.MaxAttempts is < 1 or > 5) throw new InvalidOperationException("最大尝试次数必须在 1 到 5 之间");
        if (input.MaxSteps is < 1 or > 10) throw new InvalidOperationException("最大执行步骤必须在 1 到 10 之间");
        if (input.DailyExecutionLimit is < 1 or > 1000) throw new InvalidOperationException("每日执行上限必须在 1 到 1000 之间");
        if (input.MaxTokenBudget is < 256 or > 200000) throw new InvalidOperationException("单次 Token 上限必须在 256 到 200000 之间");
        if (input.ConditionField.Length > 160 || input.ConditionValue.Length > 500)
            throw new InvalidOperationException("事件条件字段或值过长");
        if (input.ConditionOperator != AgentEventConditionOperator.None && string.IsNullOrWhiteSpace(input.ConditionField))
            throw new InvalidOperationException("配置条件运算符时必须选择条件字段");
        if (input.ConditionOperator is not AgentEventConditionOperator.None and not AgentEventConditionOperator.Exists
            && string.IsNullOrWhiteSpace(input.ConditionValue))
            throw new InvalidOperationException("该条件运算符必须填写比较值");
    }

    private async Task EnsureSystemAdminAsync(int userId, CancellationToken cancellationToken)
    {
        var allowed = await _context.Users.AsNoTracking()
            .AnyAsync(user => user.Id == userId && user.Role == UserRole.systemAdmin && user.Status == UserStatus.Active, cancellationToken);
        if (!allowed) throw new UnauthorizedAccessException("只有启用的系统管理员可以管理事件订阅规则");
    }
}

public sealed class AgentEventAutomationService
{
    private readonly ApplicationDbContext _context;
    private readonly AgentExecutionService _execution;
    private readonly AiSessionService _sessions;
    private readonly ILogger<AgentEventAutomationService> _logger;

    public AgentEventAutomationService(
        ApplicationDbContext context,
        AgentExecutionService execution,
        AiSessionService sessions,
        ILogger<AgentEventAutomationService> logger)
    {
        _context = context;
        _execution = execution;
        _sessions = sessions;
        _logger = logger;
    }

    public async Task<int> DispatchPendingEventsAsync(CancellationToken cancellationToken = default)
    {
        var ids = await _context.EventBusMessages.AsNoTracking()
            .Where(item => item.Status == EventBusMessageStatus.Pending)
            .OrderBy(item => item.CreatedAt)
            .Select(item => item.Id)
            .Take(20)
            .ToListAsync(cancellationToken);
        var handled = 0;
        foreach (var id in ids)
        {
            var claimed = await _context.EventBusMessages
                .Where(item => item.Id == id && item.Status == EventBusMessageStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, EventBusMessageStatus.Processing)
                    .SetProperty(item => item.LockedAt, AppTime.Now)
                    .SetProperty(item => item.ErrorMessage, string.Empty), cancellationToken);
            if (claimed != 1) continue;
            await DispatchClaimedEventAsync(id, cancellationToken);
            handled++;
        }
        return handled;
    }

    public async Task RecoverStaleAsync(CancellationToken cancellationToken = default)
    {
        var now = AppTime.Now;
        var staleBefore = now.AddMinutes(-15);
        await _context.EventBusMessages
            .Where(item => item.Status == EventBusMessageStatus.Processing
                && item.LockedAt.HasValue && item.LockedAt < staleBefore)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, EventBusMessageStatus.Pending)
                .SetProperty(item => item.LockedAt, (DateTime?)null)
                .SetProperty(item => item.ErrorMessage, "事件分发中断，已恢复等待处理"), cancellationToken);

        var stale = await _context.AgentEventExecutions
            .Where(item => (!item.ProjectId.HasValue || _context.Project.Any(p => p.Id == item.ProjectId && !p.IsDeleted && p.Status == ProjectStatus.Active)) && item.Status == AgentEventExecutionStatus.Running
                && item.LockedAt.HasValue
                && item.LockedAt < staleBefore)
            .Take(20)
            .ToListAsync(cancellationToken);
        foreach (var item in stale)
        {
            var exhausted = item.AttemptCount >= item.MaxAttempts;
            item.Status = exhausted ? AgentEventExecutionStatus.Failed : AgentEventExecutionStatus.Retrying;
            item.ErrorMessage = exhausted ? "事件 Agent 执行超时且已达到最大尝试次数" : "事件 Agent 执行中断，已恢复到重试队列";
            item.NextRunAt = exhausted ? now : now.AddMinutes(1);
            item.CompletedAt = exhausted ? now : null;
            item.LockedAt = null;
            item.UpdatedAt = now;
        }
        if (stale.Count > 0) await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task ResumeResolvedApprovalsAsync(CancellationToken cancellationToken = default)
    {
        var items = await _context.AgentEventExecutions
            .Where(item => (!item.ProjectId.HasValue || _context.Project.Any(p => p.Id == item.ProjectId && !p.IsDeleted && p.Status == ProjectStatus.Active)) && item.Status == AgentEventExecutionStatus.WaitingApproval && item.AiSessionId.HasValue)
            .OrderBy(item => item.UpdatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
        foreach (var item in items)
        {
            var waiting = await _context.AgentToolCalls.AsNoTracking()
                .AnyAsync(call => call.AiSessionId == item.AiSessionId
                    && call.Status == AgentToolCallStatus.PendingApproval, cancellationToken);
            if (waiting) continue;
            item.Status = AgentEventExecutionStatus.Pending;
            item.WaitingApprovalRequestId = null;
            item.NextRunAt = AppTime.Now;
            item.UpdatedAt = AppTime.Now;
        }
        if (items.Count > 0) await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<long?> ClaimNextExecutionAsync(CancellationToken cancellationToken = default)
    {
        var now = AppTime.Now;
        var id = await _context.AgentEventExecutions.AsNoTracking()
            .Where(item => (!item.ProjectId.HasValue || _context.Project.Any(p => p.Id == item.ProjectId && !p.IsDeleted && p.Status == ProjectStatus.Active)) && (item.Status == AgentEventExecutionStatus.Pending || item.Status == AgentEventExecutionStatus.Retrying)
                && item.NextRunAt <= now
                && item.AttemptCount < item.MaxAttempts)
            .OrderBy(item => item.NextRunAt)
            .ThenBy(item => item.Id)
            .Select(item => (long?)item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (!id.HasValue) return null;

        var affected = await _context.AgentEventExecutions
            .Where(item => (!item.ProjectId.HasValue || _context.Project.Any(p => p.Id == item.ProjectId && !p.IsDeleted && p.Status == ProjectStatus.Active)) && item.Id == id
                && (item.Status == AgentEventExecutionStatus.Pending || item.Status == AgentEventExecutionStatus.Retrying)
                && item.NextRunAt <= now
                && item.AttemptCount < item.MaxAttempts)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AgentEventExecutionStatus.Running)
                .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1)
                .SetProperty(item => item.LockedAt, now)
                .SetProperty(item => item.StartedAt, item => item.StartedAt ?? now)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (affected != 1) return null;

        // ExecuteUpdate 绕过变更跟踪器。同一作用域中刚分发出来的执行项仍可能缓存为 Pending，
        // 若不重新加载，紧接着的 ProcessExecutionAsync 会误判为“不是 Running”并直接返回。
        var trackedEntry = _context.ChangeTracker.Entries<AgentEventExecution>()
            .FirstOrDefault(entry => entry.Entity.Id == id.Value);
        if (trackedEntry != null)
            await trackedEntry.ReloadAsync(cancellationToken);

        return id;
    }

    public async Task ProcessExecutionAsync(long executionId, CancellationToken cancellationToken = default)
    {
        var item = await _context.AgentEventExecutions
            .Include(execution => execution.Subscription)
            .Include(execution => execution.AgentDefinition)
            .Include(execution => execution.RequestedByUser)
            .FirstOrDefaultAsync(execution => execution.Id == executionId, cancellationToken);
        if (item == null || item.Status != AgentEventExecutionStatus.Running) return;
        if (item.ProjectId.HasValue && !await _context.Project.AnyAsync(p => p.Id == item.ProjectId && !p.IsDeleted && p.Status == ProjectStatus.Active, cancellationToken))
        {
            await MarkSkippedAsync(item, ProjectLifecycleRules.ReadOnlyMessage, cancellationToken);
            return;
        }

        try
        {
            if (item.Subscription?.IsEnabled != true)
            {
                await MarkSkippedAsync(item, "订阅规则已停用", cancellationToken);
                return;
            }
            if (item.AgentDefinition?.IsEnabled != true)
            {
                await MarkSkippedAsync(item, "Agent 已停用或不存在", cancellationToken);
                return;
            }
            if (item.RequestedByUser is not { Role: UserRole.systemAdmin, Status: UserStatus.Active })
            {
                await MarkSkippedAsync(item, "规则创建者已不是启用的系统管理员", cancellationToken);
                return;
            }

            var previousToolCallId = item.AiSessionId.HasValue
                ? await _context.AgentToolCalls.AsNoTracking()
                    .Where(call => call.AiSessionId == item.AiSessionId)
                    .Select(call => (int?)call.Id)
                    .MaxAsync(cancellationToken) ?? 0
                : 0;

            AiSession session;
            string response;
            var remainingTokenBudget = Math.Max(64, item.MaxTokenBudget - item.ConsumedTokens);
            if (item.AiSessionId.HasValue)
            {
                var continuation = "系统正在继续处理事件触发任务。请读取上一轮工具结果；若目标已完成，给出最终结论；若仍需操作，只调用必要工具，不要重复已成功或已拒绝的操作。";
                (session, response) = await _execution.ContinueAsync(
                    item.AiSessionId.Value,
                    continuation,
                    item.RequestedByUser,
                    cancellationToken,
                    remainingTokenBudget);
            }
            else
            {
                (session, response) = await _execution.RunAsync(
                    item.AgentDefinition.AgentKey,
                    item.Prompt,
                    item.RequestedByUserId,
                    item.ProjectId,
                    item.TaskId,
                    cancellationToken,
                    remainingTokenBudget);
                item.AiSessionId = session.Id;
            }

            item.StepCount++;
            item.ResultSummary = response;
            item.ConsumedTokens = (session.InputTokens ?? 0) + (session.OutputTokens ?? 0);
            item.ErrorMessage = string.Empty;
            item.LockedAt = null;
            item.UpdatedAt = AppTime.Now;

            var calls = await _context.AgentToolCalls.AsNoTracking()
                .Where(call => call.AiSessionId == session.Id && call.Id > previousToolCallId)
                .OrderBy(call => call.Id)
                .ToListAsync(cancellationToken);
            var pendingApproval = calls.FirstOrDefault(call => call.Status == AgentToolCallStatus.PendingApproval);
            if (pendingApproval != null)
            {
                item.Status = AgentEventExecutionStatus.WaitingApproval;
                item.WaitingApprovalRequestId = pendingApproval.ApprovalRequestId;
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }

            // 上下文读取是每轮都会写入的审计记录，不能据此判断事件 Agent 仍需续跑。
            var hasContinuationToolCall = calls.Any(call =>
                !string.Equals(call.ToolName, AgentContextService.ContextReadToolName, StringComparison.OrdinalIgnoreCase));
            if (hasContinuationToolCall && item.StepCount < item.MaxSteps && item.ConsumedTokens < item.MaxTokenBudget)
            {
                item.Status = AgentEventExecutionStatus.Pending;
                item.NextRunAt = AppTime.Now.AddSeconds(1);
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }

            await _sessions.CloseAsync(session);
            item.Status = AgentEventExecutionStatus.Completed;
            if (item.ConsumedTokens >= item.MaxTokenBudget)
                item.ResultSummary = $"{item.ResultSummary}\n\n[系统已在 Token 预算 {item.MaxTokenBudget} 附近停止继续调用]";
            item.CompletedAt = AppTime.Now;
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await MarkFailedOrRetryAsync(executionId, ex, cancellationToken);
        }
    }

    private async Task DispatchClaimedEventAsync(long eventId, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            var message = await _context.EventBusMessages.FirstAsync(item => item.Id == eventId, cancellationToken);
            var rules = await _context.AgentEventSubscriptions.AsNoTracking()
                .Include(item => item.AgentDefinition)
                .Where(item => item.IsEnabled && item.EventType == message.EventType)
                .ToListAsync(cancellationToken);
            var (eventProjectId, taskId) = await ResolveScopeAsync(message, cancellationToken);
            var now = AppTime.Now;

            foreach (var rule in rules)
            {
                if (rule.ProjectId.HasValue && rule.ProjectId != eventProjectId) continue;
                if (await _context.AgentEventExecutions.AsNoTracking()
                    .AnyAsync(item => item.SubscriptionId == rule.Id && item.EventBusMessageId == message.Id, cancellationToken))
                    continue;

                var status = AgentEventExecutionStatus.Pending;
                var error = string.Empty;
                if (eventProjectId.HasValue &&
                    (!await _context.Project.AnyAsync(p => p.Id == eventProjectId && !p.IsDeleted && p.Status == ProjectStatus.Active, cancellationToken)
                     || await _context.ProjectActivityRecords.AnyAsync(a => a.ProjectId == eventProjectId
                         && a.FieldName == "AutomationResumeBoundary" && a.OccurredAt >= message.CreatedAt, cancellationToken)))
                {
                    status = AgentEventExecutionStatus.Skipped;
                    error = "项目已归档，或事件早于最近恢复时间；不补跑。";
                }
                else if (rule.AgentDefinition?.IsEnabled != true)
                {
                    status = AgentEventExecutionStatus.Skipped;
                    error = "Agent 已停用或不存在";
                }
                else if (rule.AgentDefinition.RequiresProject && !eventProjectId.HasValue)
                {
                    status = AgentEventExecutionStatus.Skipped;
                    error = "事件无法解析项目范围，但 Agent 要求关联项目";
                }
                else if (rule.AgentDefinition.RequiresTask && !taskId.HasValue)
                {
                    status = AgentEventExecutionStatus.Skipped;
                    error = "事件无法解析任务范围，但 Agent 要求关联任务";
                }
                else if (!MatchesCondition(rule, message, eventProjectId, taskId))
                {
                    status = AgentEventExecutionStatus.Skipped;
                    error = "事件数据不满足订阅条件";
                }
                else if (await ReachedDailyLimitAsync(rule, now, cancellationToken))
                {
                    status = AgentEventExecutionStatus.Skipped;
                    error = $"规则已达到每日 {rule.DailyExecutionLimit} 次执行上限";
                }
                else if (rule.CooldownSeconds > 0)
                {
                    var cutoff = now.AddSeconds(-rule.CooldownSeconds);
                    var inCooldown = await _context.AgentEventExecutions.AsNoTracking()
                        .AnyAsync(item => item.SubscriptionId == rule.Id
                            && item.CreatedAt >= cutoff
                            && item.Status != AgentEventExecutionStatus.Skipped
                            && item.Status != AgentEventExecutionStatus.Cancelled, cancellationToken);
                    if (inCooldown)
                    {
                        status = AgentEventExecutionStatus.Skipped;
                        error = $"规则处于 {rule.CooldownSeconds} 秒冷却期";
                    }
                }

                _context.AgentEventExecutions.Add(new AgentEventExecution
                {
                    SubscriptionId = rule.Id,
                    EventBusMessageId = message.Id,
                    AgentDefinitionId = rule.AgentDefinitionId,
                    ProjectId = eventProjectId,
                    TaskId = taskId,
                    RequestedByUserId = rule.CreatedByUserId,
                    Status = status,
                    Prompt = BuildPrompt(rule.PromptTemplate, message, eventProjectId, taskId),
                    ErrorMessage = error,
                    MaxAttempts = rule.MaxAttempts,
                    MaxSteps = rule.MaxSteps,
                    MaxTokenBudget = rule.MaxTokenBudget,
                    NextRunAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CompletedAt = status == AgentEventExecutionStatus.Skipped ? now : null
                });
            }

            message.Status = EventBusMessageStatus.Processed;
            message.ProcessedAt = now;
            message.LockedAt = null;
            message.ErrorMessage = string.Empty;
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _context.ChangeTracker.Clear();
            var message = await _context.EventBusMessages.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
            if (message == null) return;
            message.RetryCount++;
            message.Status = message.RetryCount >= 3 ? EventBusMessageStatus.Failed : EventBusMessageStatus.Pending;
            message.LockedAt = null;
            message.ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            message.ProcessedAt = message.Status == EventBusMessageStatus.Failed ? AppTime.Now : null;
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogError(ex, "事件 {EventId} 分发到 Agent 订阅失败", eventId);
        }
    }

    private async Task<bool> ReachedDailyLimitAsync(AgentEventSubscription rule, DateTime now, CancellationToken cancellationToken)
    {
        var start = now.Date;
        var end = start.AddDays(1);
        var count = await _context.AgentEventExecutions.AsNoTracking()
            .CountAsync(item => item.SubscriptionId == rule.Id
                && item.CreatedAt >= start
                && item.CreatedAt < end
                && item.Status != AgentEventExecutionStatus.Skipped
                && item.Status != AgentEventExecutionStatus.Cancelled, cancellationToken);
        return count >= rule.DailyExecutionLimit;
    }

    internal static bool MatchesCondition(
        AgentEventSubscription rule,
        EventBusMessage message,
        int? projectId,
        int? taskId)
    {
        if (rule.ConditionOperator == AgentEventConditionOperator.None || string.IsNullOrWhiteSpace(rule.ConditionField)) return true;
        var actual = ResolveConditionValue(rule.ConditionField, message, projectId, taskId);
        if (rule.ConditionOperator == AgentEventConditionOperator.Exists) return actual != null;
        if (actual == null) return false;
        var expected = rule.ConditionValue ?? string.Empty;
        return rule.ConditionOperator switch
        {
            AgentEventConditionOperator.Equals => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            AgentEventConditionOperator.NotEquals => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            AgentEventConditionOperator.Contains => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            AgentEventConditionOperator.StartsWith => actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            AgentEventConditionOperator.GreaterThan => decimal.TryParse(actual, out var leftGreater) && decimal.TryParse(expected, out var rightGreater) && leftGreater > rightGreater,
            AgentEventConditionOperator.LessThan => decimal.TryParse(actual, out var leftLess) && decimal.TryParse(expected, out var rightLess) && leftLess < rightLess,
            _ => false
        };
    }

    private static string? ResolveConditionValue(string field, EventBusMessage message, int? projectId, int? taskId)
    {
        var normalized = field.Trim();
        if (normalized.Equals("aggregateType", StringComparison.OrdinalIgnoreCase)) return message.AggregateType;
        if (normalized.Equals("aggregateId", StringComparison.OrdinalIgnoreCase)) return message.AggregateId;
        if (normalized.Equals("projectId", StringComparison.OrdinalIgnoreCase)) return projectId?.ToString();
        if (normalized.Equals("taskId", StringComparison.OrdinalIgnoreCase)) return taskId?.ToString();
        if (!normalized.StartsWith("payload.", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(message.PayloadJson) ? "{}" : message.PayloadJson);
            var current = document.RootElement;
            foreach (var segment in normalized[8..].Split('.', StringSplitOptions.RemoveEmptyEntries).Take(5))
            {
                if (current.ValueKind != JsonValueKind.Object
                    || !current.TryGetProperty(segment, out current)) return null;
            }
            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.GetRawText(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => current.GetRawText()
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(int? ProjectId, int? TaskId)> ResolveScopeAsync(
        EventBusMessage message,
        CancellationToken cancellationToken)
    {
        int? projectId = null;
        int? taskId = null;
        try
        {
            using var document = JsonDocument.Parse(message.PayloadJson);
            projectId = TryGetInt32(document.RootElement, "projectId");
            taskId = TryGetInt32(document.RootElement, "taskId");
        }
        catch (JsonException)
        {
            // 事件仍会被审计，正文会原样交给 Agent；范围继续通过聚合对象解析。
        }

        if (taskId.HasValue)
        {
            projectId = await _context.ToDoTasks.AsNoTracking()
                .Where(item => item.Id == taskId && !item.IsDeleted)
                .Select(item => (int?)item.ProjectId)
                .FirstOrDefaultAsync(cancellationToken) ?? projectId;
        }

        if (int.TryParse(message.AggregateId, out var aggregateId))
        {
            if (message.AggregateType.Equals("Project", StringComparison.OrdinalIgnoreCase))
                projectId ??= aggregateId;
            else if (message.AggregateType.Equals("Task", StringComparison.OrdinalIgnoreCase)
                || message.AggregateType.Equals("ToDoTask", StringComparison.OrdinalIgnoreCase))
            {
                taskId ??= aggregateId;
                projectId ??= await _context.ToDoTasks.AsNoTracking()
                    .Where(item => item.Id == aggregateId && !item.IsDeleted)
                    .Select(item => (int?)item.ProjectId)
                    .FirstOrDefaultAsync(cancellationToken);
            }
            else if (message.AggregateType.Equals("MeetingMinutes", StringComparison.OrdinalIgnoreCase))
                projectId ??= await _context.MeetingMinutes.AsNoTracking()
                    .Where(item => item.Id == aggregateId && !item.IsDeleted)
                    .Select(item => (int?)item.ProjectId)
                    .FirstOrDefaultAsync(cancellationToken);
            else if (message.AggregateType.Equals("ProjectDocument", StringComparison.OrdinalIgnoreCase))
                projectId ??= await _context.ProjectDocuments.AsNoTracking()
                    .Where(item => item.Id == aggregateId)
                    .Select(item => (int?)item.ProjectId)
                    .FirstOrDefaultAsync(cancellationToken);
            else if (message.AggregateType.Equals("RedBlueSession", StringComparison.OrdinalIgnoreCase))
                projectId ??= await _context.RedBlueSessions.AsNoTracking()
                    .Where(item => item.Id == aggregateId)
                    .Select(item => (int?)item.ProjectId)
                    .FirstOrDefaultAsync(cancellationToken);
        }
        return (projectId, taskId);
    }

    private static int? TryGetInt32(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var number)) return number;
            if (property.Value.ValueKind == JsonValueKind.String && int.TryParse(property.Value.GetString(), out number)) return number;
        }
        return null;
    }

    private static string BuildPrompt(string template, EventBusMessage message, int? projectId, int? taskId)
    {
        var payload = message.PayloadJson.Length > 12000 ? message.PayloadJson[..12000] : message.PayloadJson;
        var prompt = template
            .Replace("{eventType}", message.EventType, StringComparison.OrdinalIgnoreCase)
            .Replace("{aggregateType}", message.AggregateType, StringComparison.OrdinalIgnoreCase)
            .Replace("{aggregateId}", message.AggregateId, StringComparison.OrdinalIgnoreCase)
            .Replace("{projectId}", projectId?.ToString() ?? "未解析", StringComparison.OrdinalIgnoreCase)
            .Replace("{taskId}", taskId?.ToString() ?? "未解析", StringComparison.OrdinalIgnoreCase)
            .Replace("{payload}", payload, StringComparison.OrdinalIgnoreCase);
        return prompt.Length > 20000 ? prompt[..20000] : prompt;
    }

    private async Task MarkSkippedAsync(AgentEventExecution item, string reason, CancellationToken cancellationToken)
    {
        item.Status = AgentEventExecutionStatus.Skipped;
        item.ErrorMessage = reason;
        item.LockedAt = null;
        item.CompletedAt = AppTime.Now;
        item.UpdatedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkFailedOrRetryAsync(long executionId, Exception exception, CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        var item = await _context.AgentEventExecutions.FirstOrDefaultAsync(execution => execution.Id == executionId, cancellationToken);
        if (item == null) return;
        var exhausted = item.AttemptCount >= item.MaxAttempts;
        var now = AppTime.Now;
        item.Status = exhausted ? AgentEventExecutionStatus.Failed : AgentEventExecutionStatus.Retrying;
        item.ErrorMessage = exception.Message.Length > 2000 ? exception.Message[..2000] : exception.Message;
        item.LockedAt = null;
        item.UpdatedAt = now;
        item.CompletedAt = exhausted ? now : null;
        item.NextRunAt = exhausted ? now : now.AddMinutes(item.AttemptCount <= 1 ? 1 : item.AttemptCount == 2 ? 5 : 15);
        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogWarning(exception, "事件 Agent 执行 {ExecutionId} 第 {Attempt}/{MaxAttempts} 次失败", executionId, item.AttemptCount, item.MaxAttempts);
    }
}

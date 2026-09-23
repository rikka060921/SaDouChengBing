using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public sealed record AgentDispatchCandidate(
    int AgentDefinitionId,
    string AgentKey,
    string AgentName,
    double Score,
    double CapabilityScore,
    double ProjectPermissionScore,
    double RiskScore,
    double LoadScore,
    double HistoryScore,
    int ActiveWorkItems,
    int HistoricalRuns,
    double HistoricalSuccessRate,
    IReadOnlyList<string> Reasons,
    int AcceptedDeliveries = 0,
    int RejectedDeliveries = 0,
    double FeedbackScore = 0);

public sealed record AgentDispatchResult(
    AgentDispatchDecision Decision,
    IReadOnlyList<AgentDispatchCandidate> Candidates,
    bool AssignedAutomatically);

/// <summary>
/// 按注册职责匹配任务；权限是准入条件，历史表现只用于事后复盘。
/// </summary>
public class AgentDispatchService
{
    public const string DispatcherAgentKey = "task-dispatcher";
    private readonly ApplicationDbContext _context;
    private readonly AgentWorkQueueService _workQueue;
    private readonly IEventBus _eventBus;
    private readonly AgentOutcomeService _outcomes;
    private readonly ILogger<AgentDispatchService>? _logger;

    public AgentDispatchService(
        ApplicationDbContext context,
        AgentWorkQueueService workQueue,
        IEventBus eventBus,
        AgentOutcomeService outcomes,
        ILogger<AgentDispatchService>? logger = null)
    {
        _context = context;
        _workQueue = workQueue;
        _eventBus = eventBus;
        _outcomes = outcomes;
        _logger = logger;
    }

    public async Task<AgentDispatchResult> DispatchTaskAsync(
        int taskId,
        int requestedByUserId,
        bool forceConfirmation = false,
        CancellationToken cancellationToken = default)
    {
        var task = await _context.ToDoTasks
            .Include(item => item.Project)
            .Include(item => item.LabelLinks).ThenInclude(link => link.Label)
            .FirstOrDefaultAsync(item => item.Id == taskId && !item.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("待调度任务不存在");
        await EnsureCanDispatchAsync(task, requestedByUserId, cancellationToken);
        if (task.AssigneeType != TaskAssigneeType.DigitalEmployee)
            throw new InvalidOperationException("只有数字员工任务可以自动调度");
        if (task.Status is ToDo.Entities.TaskStatus.Completed or ToDo.Entities.TaskStatus.Cancelled)
            throw new InvalidOperationException("已完成或已取消的任务不能调度");

        await _context.AgentDispatchDecisions
            .Where(item => item.TaskId == task.Id
                && item.DispatchVersion != task.AgentAssignmentVersion
                && item.Status == AgentDispatchDecisionStatus.PendingConfirmation)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AgentDispatchDecisionStatus.Superseded)
                .SetProperty(item => item.UpdatedAt, AppTime.Now)
                .SetProperty(item => item.ResolvedAt, AppTime.Now), cancellationToken);

        var existing = await _context.AgentDispatchDecisions
            .Include(item => item.RecommendedAgentDefinition)
            .FirstOrDefaultAsync(item => item.TaskId == task.Id && item.DispatchVersion == task.AgentAssignmentVersion, cancellationToken);
        if (existing != null)
            return new AgentDispatchResult(existing, ParseCandidates(existing.CandidatesJson), existing.Status == AgentDispatchDecisionStatus.AutoAssigned);

        var blockers = new HashSet<string>();
        var candidates = await MatchCandidatesAsync(task, cancellationToken, blockers);
        var top = candidates.FirstOrDefault();
        if (!forceConfirmation && candidates.Count > 1)
        {
            var ids = candidates.Select(c => c.AgentDefinitionId).ToArray();
            var loads = await _context.AgentWorkItems.AsNoTracking()
                .Where(w => ids.Contains(w.AgentDefinitionId) && w.Status != AgentWorkItemStatus.Completed
                    && w.Status != AgentWorkItemStatus.Cancelled && w.Status != AgentWorkItemStatus.Failed)
                .GroupBy(w => w.AgentDefinitionId)
                .Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Id, g => g.Count, cancellationToken);
            top = candidates.OrderBy(c => loads.GetValueOrDefault(c.AgentDefinitionId)).ThenBy(c => c.AgentDefinitionId).First();
        }
        // 保留旧数据库字段以兼容历史记录；新流程不计算或展示置信度。
        var confidence = 0d;
        var autoAssign = !forceConfirmation && top != null;
        var decision = new AgentDispatchDecision
        {
            TaskId = task.Id,
            ProjectId = task.ProjectId,
            RequestedByUserId = requestedByUserId,
            DispatchVersion = task.AgentAssignmentVersion,
            RecommendedAgentDefinitionId = autoAssign || candidates.Count == 1 ? top?.AgentDefinitionId : null,
            SelectedAgentDefinitionId = autoAssign ? top?.AgentDefinitionId : null,
            Status = top == null
                ? AgentDispatchDecisionStatus.NoCandidate
                : autoAssign ? AgentDispatchDecisionStatus.AutoAssigned : AgentDispatchDecisionStatus.PendingConfirmation,
            Confidence = confidence,
            CandidatesJson = JsonSerializer.Serialize(candidates),
            Explanation = autoAssign && candidates.Count > 1
                ? $"已从 {candidates.Count} 个职责及授权匹配的 Agent 中，按待处理工作量选择「{top!.AgentName}」；工作量相同时按注册顺序选择，无需人工挑选。"
                : candidates.Count == 0 && blockers.Count > 0
                    ? string.Join("；", blockers) : BuildExplanation(candidates, autoAssign),
            CreatedAt = AppTime.Now,
            UpdatedAt = AppTime.Now,
            ResolvedAt = autoAssign ? AppTime.Now : null
        };
        _context.AgentDispatchDecisions.Add(decision);

        if (autoAssign && top != null)
        {
            ApplyAssignment(task, top.AgentDefinitionId, top.AgentName);
        }
        else
        {
            task.AgentDefinitionId = null;
            task.AgentName = null;
            task.AgentExecutionStatus = AgentTaskExecutionStatus.AwaitingDispatchConfirmation;
            task.AgentLastError = top == null ? "没有找到职责及权限均匹配的 Agent，请修改任务或改为人工处理" : string.Empty;
            task.UpdatedAt = AppTime.Now;
        }
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // 请求线程与后台补偿可能同时调度；唯一索引保证每个任务版本只产生一份决策。
            _context.ChangeTracker.Clear();
            var duplicate = await _context.AgentDispatchDecisions.AsNoTracking()
                .FirstOrDefaultAsync(item => item.TaskId == task.Id
                    && item.DispatchVersion == task.AgentAssignmentVersion, cancellationToken);
            if (duplicate == null) throw;
            return new AgentDispatchResult(
                duplicate,
                ParseCandidates(duplicate.CandidatesJson),
                duplicate.Status == AgentDispatchDecisionStatus.AutoAssigned);
        }

        if (autoAssign && top != null)
            await EnqueueAssignedTaskAsync(task, requestedByUserId, cancellationToken);

        await _eventBus.PublishAsync(
            autoAssign ? "agent.dispatch.auto-assigned" : "agent.dispatch.confirmation-required",
            new { decision.Id, taskId = task.Id, projectId = task.ProjectId, recommendedAgentId = decision.RecommendedAgentDefinitionId, matchingMode = "registered-capability", candidateCount = candidates.Count },
            "AgentDispatchDecision",
            decision.Id.ToString(),
            cancellationToken);
        return new AgentDispatchResult(decision, candidates, autoAssign);
    }

    public async Task<AgentDispatchDecision> ConfirmAsync(
        int decisionId,
        int selectedAgentDefinitionId,
        int operatedByUserId,
        CancellationToken cancellationToken = default)
    {
        var decision = await _context.AgentDispatchDecisions
            .Include(item => item.Task)
            .FirstOrDefaultAsync(item => item.Id == decisionId, cancellationToken)
            ?? throw new InvalidOperationException("调度决策不存在");
        var task = decision.Task ?? throw new InvalidOperationException("调度任务不存在");
        await EnsureCanDispatchAsync(task, operatedByUserId, cancellationToken);
        if (decision.Status != AgentDispatchDecisionStatus.PendingConfirmation)
            throw new InvalidOperationException("该调度决策已经处理");
        if (task.AssigneeType != TaskAssigneeType.DigitalEmployee
            || task.Status is ToDo.Entities.TaskStatus.Completed or ToDo.Entities.TaskStatus.Cancelled)
            throw new InvalidOperationException("该任务当前不能确认 Agent 派单");
        if (decision.DispatchVersion != task.AgentAssignmentVersion)
            throw new InvalidOperationException("任务分配版本已变化，请刷新后重新调度");

        var allowedCandidateIds = ParseCandidates(decision.CandidatesJson)
            .Select(item => item.AgentDefinitionId)
            .ToHashSet();
        if (!allowedCandidateIds.Contains(selectedAgentDefinitionId))
            throw new UnauthorizedAccessException("只能选择本次职责匹配中的候选 Agent");
        var selected = await _context.AgentDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == selectedAgentDefinitionId
                && item.IsEnabled
                && item.CanReceiveTaskDispatch
                && item.AgentKey != DispatcherAgentKey, cancellationToken)
            ?? throw new InvalidOperationException("所选 Agent 已停用或不再允许自动接单");

        // 确认前重新检查职责与当前项目授权，不能沿用已经失效的候选快照。
        await _context.Entry(task).Collection(item => item.LabelLinks).Query()
            .Include(item => item.Label).LoadAsync(cancellationToken);
        var currentCandidates = await MatchCandidatesAsync(task, cancellationToken);
        if (!currentCandidates.Any(item => item.AgentDefinitionId == selected.Id))
            throw new InvalidOperationException("所选 Agent 的职责或项目授权已变化，请修改任务后重新匹配");

        decision.SelectedAgentDefinitionId = selected.Id;
        decision.Status = AgentDispatchDecisionStatus.Confirmed;
        decision.ResolvedAt = AppTime.Now;
        decision.UpdatedAt = AppTime.Now;
        ApplyAssignment(task, selected.Id, selected.Name);
        await _outcomes.RecordDispatchSelectionAsync(decision, selected.Id, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        await EnqueueAssignedTaskAsync(task, operatedByUserId, cancellationToken);
        await _eventBus.PublishAsync(
            "agent.dispatch.confirmed",
            new { decision.Id, taskId = task.Id, projectId = task.ProjectId, selectedAgentId = selected.Id, operatedByUserId },
            "AgentDispatchDecision",
            decision.Id.ToString(),
            cancellationToken);
        return decision;
    }

    public Task<AgentDispatchDecision?> GetLatestAsync(int taskId, CancellationToken cancellationToken = default)
        => _context.AgentDispatchDecisions.AsNoTracking()
            .Include(item => item.RecommendedAgentDefinition)
            .Include(item => item.SelectedAgentDefinition)
            .Where(item => item.TaskId == taskId)
            .OrderByDescending(item => item.DispatchVersion)
            .ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>补偿请求中断：为已经标记自动调度但尚无本版本决策的任务补建调度。</summary>
    public async Task EnsurePendingDispatchesAsync(CancellationToken cancellationToken = default)
    {
        // 权限过滤必须在 Take 之前：失效发起人的旧任务不能占满批次，阻塞其他项目。
        var pending = await GetDispatchableTasks().AsNoTracking()
            .Include(item => item.Project)
            .Where(item => !item.IsDeleted
                && item.AssigneeType == TaskAssigneeType.DigitalEmployee
                && !item.AgentDefinitionId.HasValue
                && item.AgentExecutionStatus == AgentTaskExecutionStatus.AwaitingDispatchConfirmation
                && item.Status != ToDo.Entities.TaskStatus.Completed
                && item.Status != ToDo.Entities.TaskStatus.Cancelled
                && !_context.AgentDispatchDecisions.Any(decision => decision.TaskId == item.Id
                    && decision.DispatchVersion == item.AgentAssignmentVersion))
            .OrderBy(item => item.UpdatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var task in pending)
        {
            var requesterId = task.CreatorId > 0
                ? task.CreatorId
                : task.Project?.LeaderUserId ?? 0;
            if (requesterId <= 0) continue;
            try
            {
                await DispatchTaskAsync(task.Id, requesterId, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            {
                // 查询后仍可能撤权或改派；只跳过当前任务，后续补偿继续，且不提升发起人权限。
                _logger?.LogWarning(ex, "跳过当前已不可调度的 Agent 任务 {TaskId}", task.Id);
            }
        }
    }

    public static IReadOnlyList<AgentDispatchCandidate> ParseCandidates(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<AgentDispatchCandidate>>(json ?? "[]") ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<List<AgentDispatchCandidate>> MatchCandidatesAsync(ToDoTask task, CancellationToken cancellationToken,
        ISet<string>? blockers = null)
    {
        var agents = await _context.AgentDefinitions.AsNoTracking()
            .Include(item => item.ToolPermissions)
            .Where(item => item.AgentKey != DispatcherAgentKey)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var permissions = await _context.AgentDocumentPermissions.AsNoTracking()
            .Where(item => item.ProjectId == task.ProjectId)
            .ToListAsync(cancellationToken);
        var intentPolicy = AgentTaskIntentMatcher.ForTask(task);
        var intents = intentPolicy.Intents;
        if (intents.Count == 0)
        {
            blockers?.Add("暂不能识别任务用途，请说明希望得到的成果；需要保存或创建记录时写明对象和动作");
            return [];
        }

        var candidates = new List<AgentDispatchCandidate>();
        foreach (var current in agents)
        {
            // 历史草稿可能仍由旧的稳定版本接单；匹配必须使用实际运行配置。
            var agent = current;
            if (current.StableVersion > 0 && current.StableVersion != current.Version)
            {
                var runtime = await new AgentDefinitionSnapshotService(_context)
                    .LoadRuntimeVersionAsync(current, current.StableVersion, cancellationToken);
                if (runtime == null) { blockers?.Add("历史运行版本缺失，请维护人员检查配置历史"); continue; }
                agent = runtime;
            }
            var capabilities = AgentAdministrationService.ParseCapabilities(agent.CapabilitiesJson)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (intents.Any(intent => !intent.Capabilities.Any(capabilities.Contains))) continue;
            if (!current.IsEnabled || current.LifecycleStatus == AgentLifecycleStatus.Archived
                || !current.CanReceiveTaskDispatch || !agent.CanReceiveTaskDispatch)
            {
                blockers?.Add("匹配用途的 Agent 已停用、归档或未开放自动接单，请管理员检查启用状态");
                continue;
            }
            var contexts = AgentAdministrationService.ParseContextSources(agent.ContextSourcesJson);
            var grants = permissions.Where(item => string.Equals(item.AgentKey, agent.AgentKey, StringComparison.OrdinalIgnoreCase)).ToList();
            if ((contexts.Contains(AgentContextSource.Documents) || intents.Any(i => i.RequiresDocuments))
                && (!contexts.Contains(AgentContextSource.Documents) || !grants.Any(item => item.CanRead)))
            {
                blockers?.Add("匹配用途但缺少项目资料读取范围，请在技术维护启用资料上下文，并由项目管理员授予分类读取权限");
                continue;
            }
            var missingTools = intentPolicy.Tools.Where(required => !agent.ToolPermissions.Any(t => t.IsEnabled && t.ToolName == required)).ToList();
            if (missingTools.Count > 0)
            {
                blockers?.Add("任务明确要求业务操作或联网，但匹配的 Agent 缺少对应工具，请维护人员核对工具授权");
                continue;
            }
            if (intentPolicy.Tools.Contains("project.document.write") && !grants.Any(item => item.CanWrite))
            {
                blockers?.Add("已配置资料写入工具，但缺少当前项目的资料分类写权限，请项目管理员授权");
                continue;
            }

            var reasons = intents.Select(intent => $"职责匹配：{intent.Label}").ToList();
            reasons.Add("已检查当前项目的数据范围及所需工具授权；实际写入仍按工具规则审批");
            // 旧评分属性仅用于读取历史 JSON，不再参与选择，也不编造新评分。
            candidates.Add(new AgentDispatchCandidate(current.Id, current.AgentKey, current.Name,
                0, 0, 0, 0, 0, 0, 0, 0, 0, reasons));
        }
        if (candidates.Count == 0 && (blockers == null || blockers.Count == 0))
            blockers?.Add("没有 Agent 同时具备该任务所需职责，请新建对应用途的助手；多个独立用途可拆成独立任务");
        return candidates;
    }

    private static string BuildExplanation(IReadOnlyList<AgentDispatchCandidate> candidates, bool autoAssign)
    {
        if (candidates.Count == 0)
            return "未找到职责及当前项目权限均匹配的 Agent。请明确任务用途、检查资料和工具授权，或改为人工处理。";
        if (autoAssign)
            return $"「{candidates[0].AgentName}」是唯一匹配的 Agent，已进入执行队列。{string.Join("；", candidates[0].Reasons)}";
        return candidates.Count == 1
            ? $"已找到匹配的 Agent「{candidates[0].AgentName}」，本次要求人工确认后执行。"
            : $"有 {candidates.Count} 个 Agent 的注册职责匹配，请选择执行者。候选按注册顺序列出，不计算综合分或优劣排名。";
    }

    private static void ApplyAssignment(ToDoTask task, int agentId, string agentName)
    {
        task.AssigneeId = null;
        task.AgentDefinitionId = agentId;
        task.AgentName = agentName;
        task.AgentExecutionStatus = AgentTaskExecutionStatus.Pending;
        task.AgentLastError = string.Empty;
        task.UpdatedAt = AppTime.Now;
    }

    private Task<AgentWorkItem?> EnqueueAssignedTaskAsync(ToDoTask task, int requestedByUserId, CancellationToken cancellationToken)
        => _workQueue.EnqueueTaskAsync(
            task.Id,
            requestedByUserId,
            AgentWorkTriggerType.TaskAssigned,
            task.Id,
            AgentWorkQueueService.BuildAssignmentPrompt(task),
            $"task-assigned:{task.Id}:{task.AgentDefinitionId}:{task.AgentAssignmentVersion}",
            cancellationToken: cancellationToken);

    private async Task EnsureCanDispatchAsync(ToDoTask task, int userId, CancellationToken cancellationToken)
    {
        var canEdit = await GetDispatchableTasks(userId).AnyAsync(item => item.Id == task.Id, cancellationToken);
        if (!canEdit) throw new UnauthorizedAccessException("当前用户无权确认该项目的 Agent 派单");
    }

    // 入口与后台补偿共用当前权限；创建过任务不等于离开项目后仍有操作权。
    // 后台保留原发起人，不自动借用管理员身份接管其任务。
    private IQueryable<ToDoTask> GetDispatchableTasks(int? requestedByUserId = null)
        => _context.ToDoTasks.Where(task => !task.IsDeleted
            && _context.Project.Any(project => project.Id == task.ProjectId && !project.IsDeleted
                && _context.Users.Any(user => user.Id == (requestedByUserId
                        ?? (task.CreatorId > 0 ? task.CreatorId : project.LeaderUserId))
                    && !user.IsDeleted && user.Status == UserStatus.Active
                    && (user.Role == UserRole.systemAdmin || project.LeaderUserId == user.Id
                        || _context.ProjectUsers.Any(member => member.ProjectId == project.Id
                            && member.UserId == user.Id
                            && (member.ProjectRole == (int)ProjectRole.Admin || task.CreatorId == user.Id))))));
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public sealed class AgentAdministrationService
{
    private static readonly Regex KeyPattern = new("^[a-z0-9][a-z0-9-]{1,79}$", RegexOptions.Compiled);
    private static readonly Regex CapabilityPattern = new("^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.Compiled);
    private readonly ApplicationDbContext _context;
    private readonly IAgentToolCatalog _toolCatalog;

    public AgentAdministrationService(ApplicationDbContext context, IAgentToolCatalog toolCatalog)
    {
        _context = context;
        _toolCatalog = toolCatalog;
    }

    public Task<List<AgentDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return _context.AgentDefinitions.AsNoTracking()
            .Include(item => item.ToolPermissions)
            .Include(item => item.AcceptanceContract)
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
    }

    public Task<AgentDefinition?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        return _context.AgentDefinitions.AsNoTracking()
            .Include(item => item.ToolPermissions)
            .Include(item => item.AcceptanceContract)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
    }

    public async Task<AgentDefinition> SaveAsync(
        int? id,
        AgentDefinition input,
        IEnumerable<AgentContextSource> contextSources,
        IEnumerable<string> capabilityTags,
        IEnumerable<string> enabledTools,
        IEnumerable<string> approvalTools,
        AgentAcceptanceContract acceptanceContract,
        int? changedByUserId = null,
        CancellationToken cancellationToken = default,
        bool applyImmediately = false)
    {
        NormalizeAndValidate(input);
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var normalizedKey = input.AgentKey.Trim().ToLowerInvariant();
        var duplicate = await _context.AgentDefinitions.AsNoTracking()
            .AnyAsync(item => item.AgentKey == normalizedKey && (!id.HasValue || item.Id != id.Value), cancellationToken);
        if (duplicate) throw new InvalidOperationException($"Agent Key 已存在：{normalizedKey}");

        AgentDefinition entity;
        var keepStableDeployment = false;
        if (id.HasValue)
        {
            entity = await _context.AgentDefinitions
                .Include(item => item.ToolPermissions)
                .Include(item => item.AcceptanceContract)
                .FirstOrDefaultAsync(item => item.Id == id.Value, cancellationToken)
                ?? throw new InvalidOperationException("Agent 不存在");
            if (entity.LifecycleStatus == AgentLifecycleStatus.Archived)
                throw new InvalidOperationException("已归档 Agent 不能修改，请新建配置");
            if (!string.Equals(entity.AgentKey, normalizedKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Agent 创建后不能修改 Agent Key");
            keepStableDeployment = entity.IsEnabled
                && entity.LifecycleStatus == AgentLifecycleStatus.Published;
            if (entity.StableVersion <= 0) entity.StableVersion = entity.Version;
        }
        else
        {
            entity = new AgentDefinition
            {
                AgentKey = normalizedKey,
                LifecycleStatus = AgentLifecycleStatus.Draft,
                IsEnabled = false,
                CreatedAt = AppTime.Now
            };
            _context.AgentDefinitions.Add(entity);
        }

        var shouldIncrementVersion = id.HasValue;

        entity.Name = input.Name.Trim();
        entity.Description = input.Description.Trim();
        entity.SystemPrompt = input.SystemPrompt.Trim();
        entity.ModelName = input.ModelName.Trim();
        entity.Temperature = input.Temperature;
        entity.MaxTokens = input.MaxTokens;
        entity.MaxTurns = input.MaxTurns;
        entity.TimeoutSeconds = input.TimeoutSeconds;
        entity.TemplateKey = (input.TemplateKey ?? string.Empty).Trim().ToLowerInvariant();
        if (entity.TemplateKey.Length > 64)
            throw new InvalidOperationException("模板标识不能超过 64 个字符");
        var selectedContextSources = contextSources.Distinct().ToHashSet();
        var requiresTaskContext = selectedContextSources.Contains(AgentContextSource.SelectedTask)
            || selectedContextSources.Contains(AgentContextSource.TaskComments);
        var requiresProjectContext = selectedContextSources.Count > 0;
        entity.RequiresTask = input.RequiresTask || requiresTaskContext;
        entity.RequiresProject = input.RequiresProject || entity.RequiresTask || requiresProjectContext;
        entity.AutoCommentOnCompletion = input.AutoCommentOnCompletion;
        entity.CanReceiveTaskDispatch = input.CanReceiveTaskDispatch && entity.AgentKey != AgentDispatchService.DispatcherAgentKey;
        if (entity.AutoCommentOnCompletion && !entity.RequiresTask)
            throw new InvalidOperationException("自动写入任务评论的 Agent 必须要求关联任务");
        entity.ContextSourcesJson = SerializeContextSources(selectedContextSources);
        var normalizedCapabilities = capabilityTags
            .Select(item => item.Trim().ToLowerInvariant())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item)
            .ToList();
        if (normalizedCapabilities.Any(item => !CapabilityPattern.IsMatch(item)))
            throw new InvalidOperationException("能力标签只能包含小写字母、数字、点、下划线和连字符，单项最长 80 个字符");
        if (normalizedCapabilities.Count > 50)
            throw new InvalidOperationException("能力标签最多配置 50 项");
        var capabilitiesJson = JsonSerializer.Serialize(normalizedCapabilities);
        if (capabilitiesJson.Length > 2000)
            throw new InvalidOperationException("能力标签总长度过长");
        entity.CapabilitiesJson = capabilitiesJson;
        // 新建或修改配置一律回到草稿，并撤销旧版本的发布准入；避免未测试配置直接运行。
        entity.LifecycleStatus = AgentLifecycleStatus.Draft;
        entity.IsEnabled = keepStableDeployment;
        entity.DeploymentStatus = AgentDeploymentStatus.Stable;
        entity.CanaryVersion = null;
        entity.CanaryPercent = 0;
        if (entity.IsSystemManaged) entity.HasLocalOverrides = true;
        entity.PublicationGatePassed = false;
        entity.LastTestStatus = AgentTestRunStatus.NotRun;
        entity.LastTestAt = null;
        entity.LastTestSessionId = null;
        if (shouldIncrementVersion) entity.Version++;
        entity.UpdatedAt = AppTime.Now;

        await _context.SaveChangesAsync(cancellationToken);
        await SaveAcceptanceContractAsync(entity, acceptanceContract, cancellationToken);
        await SaveToolPermissionsAsync(entity, enabledTools, approvalTools, cancellationToken);
        if (applyImmediately)
        {
            // 管理员注册即生效；快照、工具风险下限和资料授权保持独立。
            entity.IsEnabled = input.IsEnabled;
            entity.LifecycleStatus = input.IsEnabled ? AgentLifecycleStatus.Published : AgentLifecycleStatus.Paused;
            entity.StableVersion = entity.Version;
            await _context.SaveChangesAsync(cancellationToken);
        }
        await SaveVersionSnapshotAsync(entity.Id, changedByUserId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return entity;
    }

    public async Task<AgentDefinition> SaveProfileAsync(int id, int expectedVersion, AgentProfileInput input,
        int userId, CancellationToken ct = default)
    {
        System.ComponentModel.DataAnnotations.Validator.ValidateObject(input,
            new System.ComponentModel.DataAnnotations.ValidationContext(input), true);
        if (!await _context.Users.AsNoTracking().AnyAsync(u => u.Id == userId && !u.IsDeleted
            && u.Status == UserStatus.Active && u.Role == UserRole.systemAdmin, ct))
            throw new UnauthorizedAccessException("只有有效系统管理员可以修改 Agent。");
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);
        var agent = await GetAsync(id, ct) ?? throw new InvalidOperationException("Agent 不存在。");
        if (agent.LifecycleStatus == AgentLifecycleStatus.Archived)
            throw new InvalidOperationException("已归档的 Agent 不能编辑。");
        if (agent.Version != expectedVersion)
            throw new InvalidOperationException("配置已被其他人修改，请先复制本次输入，再重新打开编辑页。");
        if (agent.StableVersion > 0 && agent.StableVersion != agent.Version)
            throw new InvalidOperationException("该 Agent 存在尚未生效的历史配置，请先由维护人员处理，避免意外启用其他改动。");
        if (agent.DeploymentStatus == AgentDeploymentStatus.Canary)
            throw new InvalidOperationException("该 Agent 正在进行版本切换，请先由维护人员结束后再编辑。");
        var name = input.Name.Trim();
        var description = input.Description.Trim();
        var prompt = input.Instructions.Trim();
        if (name.Length == 0 || description.Length == 0 || prompt.Length == 0)
            throw new InvalidOperationException("请填写名称、用途说明和工作要求。");
        if (agent.Name == name && agent.Description == description && agent.SystemPrompt == prompt)
            return agent;
        // 只更新日常编辑字段；不能因缺少表单字段而重置原模型、工具权限或停用状态。
        var affected = await _context.AgentDefinitions.Where(a => a.Id == id && a.Version == expectedVersion
                && a.IsEnabled == agent.IsEnabled && a.LifecycleStatus == agent.LifecycleStatus
                && a.StableVersion == agent.StableVersion && a.DeploymentStatus == agent.DeploymentStatus)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.Name, name)
                .SetProperty(a => a.Description, description).SetProperty(a => a.SystemPrompt, prompt)
                .SetProperty(a => a.Version, expectedVersion + 1).SetProperty(a => a.StableVersion, expectedVersion + 1)
                .SetProperty(a => a.HasLocalOverrides, a => a.IsSystemManaged || a.HasLocalOverrides)
                .SetProperty(a => a.LastTestStatus, AgentTestRunStatus.NotRun)
                .SetProperty(a => a.LastTestAt, (DateTime?)null).SetProperty(a => a.LastTestSessionId, (int?)null)
                .SetProperty(a => a.PublicationGatePassed, false).SetProperty(a => a.UpdatedAt, AppTime.Now), ct);
        if (affected != 1) throw new InvalidOperationException("Agent 状态已变化，请先复制输入，再重新打开编辑页。");
        await SaveVersionSnapshotAsync(id, userId, ct);
        await transaction.CommitAsync(ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task EnableAsync(int id, int? changedByUserId = null, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var entity = await _context.AgentDefinitions.Include(item => item.AcceptanceContract)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Agent 不存在");
        if (entity.LifecycleStatus == AgentLifecycleStatus.Archived)
            throw new InvalidOperationException("已归档 Agent 不能启用");
        NormalizeAndValidate(entity);
        if (entity.AcceptanceContract == null)
            throw new InvalidOperationException("请先在编辑页补充交付要求");
        var errors = ValidateAcceptanceContract(entity.AcceptanceContract);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
        entity.IsEnabled = true;
        entity.LifecycleStatus = AgentLifecycleStatus.Published;
        entity.StableVersion = entity.Version;
        entity.DeploymentStatus = AgentDeploymentStatus.Stable;
        entity.CanaryVersion = null;
        entity.CanaryPercent = 0;
        entity.UpdatedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
        await SaveVersionSnapshotAsync(id, changedByUserId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ToggleAsync(int id, int? changedByUserId = null, CancellationToken cancellationToken = default)
    {
        var entity = await _context.AgentDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Agent 不存在");
        if (entity.LifecycleStatus == AgentLifecycleStatus.Published)
            await PauseAsync(id, changedByUserId, cancellationToken);
        else
            await PublishAsync(id, changedByUserId, cancellationToken);
    }

    public async Task PublishAsync(int id, int? changedByUserId = null, CancellationToken cancellationToken = default)
    {
        var entity = await _context.AgentDefinitions
            .Include(item => item.AcceptanceContract)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Agent 不存在");
        if (entity.LifecycleStatus == AgentLifecycleStatus.Archived)
            throw new InvalidOperationException("已归档 Agent 不能发布");
        var readinessErrors = GetPublicationReadinessErrors(entity);
        if (readinessErrors.Count > 0)
            throw new InvalidOperationException(string.Join("；", readinessErrors));

        entity.LifecycleStatus = AgentLifecycleStatus.Published;
        entity.IsEnabled = true;
        entity.StableVersion = entity.Version;
        entity.CanaryVersion = null;
        entity.CanaryPercent = 0;
        entity.DeploymentStatus = AgentDeploymentStatus.Stable;
        entity.UpdatedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task StartCanaryAsync(
        int id,
        int percent,
        int? changedByUserId = null,
        CancellationToken cancellationToken = default)
    {
        percent = Math.Clamp(percent, 1, 50);
        var entity = await _context.AgentDefinitions.Include(item => item.AcceptanceContract)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Agent 不存在");
        var readinessErrors = GetPublicationReadinessErrors(entity);
        if (readinessErrors.Count > 0) throw new InvalidOperationException(string.Join("；", readinessErrors));
        if (entity.StableVersion <= 0) throw new InvalidOperationException("尚无稳定版本，请先完整发布一次");
        if (entity.StableVersion == entity.Version) throw new InvalidOperationException("当前没有待灰度的新版本");
        var stableSnapshotExists = await _context.AgentDefinitionVersions.AsNoTracking()
            .AnyAsync(item => item.AgentDefinitionId == id && item.Version == entity.StableVersion, cancellationToken);
        if (!stableSnapshotExists) throw new InvalidOperationException("稳定版本快照缺失，不能安全启动灰度");
        entity.CanaryVersion = entity.Version;
        entity.CanaryPercent = percent;
        entity.DeploymentStatus = AgentDeploymentStatus.Canary;
        entity.LifecycleStatus = AgentLifecycleStatus.Published;
        entity.IsEnabled = true;
        entity.UpdatedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task PromoteCanaryAsync(int id, int? changedByUserId = null, CancellationToken cancellationToken = default)
    {
        var entity = await _context.AgentDefinitions.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Agent 不存在");
        if (entity.DeploymentStatus != AgentDeploymentStatus.Canary || !entity.CanaryVersion.HasValue)
            throw new InvalidOperationException("当前没有正在运行的灰度版本");
        entity.StableVersion = entity.CanaryVersion.Value;
        entity.CanaryVersion = null;
        entity.CanaryPercent = 0;
        entity.DeploymentStatus = AgentDeploymentStatus.Stable;
        entity.LifecycleStatus = AgentLifecycleStatus.Published;
        entity.IsEnabled = true;
        entity.UpdatedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task StopCanaryAsync(int id, int? changedByUserId = null, CancellationToken cancellationToken = default)
    {
        var entity = await _context.AgentDefinitions.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Agent 不存在");
        if (entity.DeploymentStatus != AgentDeploymentStatus.Canary)
            throw new InvalidOperationException("当前没有正在运行的灰度版本");
        entity.CanaryVersion = null;
        entity.CanaryPercent = 0;
        entity.DeploymentStatus = AgentDeploymentStatus.Stable;
        entity.LifecycleStatus = AgentLifecycleStatus.Published;
        entity.IsEnabled = true;
        entity.UpdatedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task RollbackToVersionAsync(
        int id,
        int targetVersion,
        int? changedByUserId = null,
        CancellationToken cancellationToken = default)
    {
        var entity = await _context.AgentDefinitions
            .Include(item => item.ToolPermissions)
            .Include(item => item.AcceptanceContract)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Agent 不存在");
        var snapshot = await _context.AgentDefinitionVersions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.AgentDefinitionId == id && item.Version == targetVersion, cancellationToken)
            ?? throw new InvalidOperationException("目标版本快照不存在");
        var wasTested = targetVersion == entity.StableVersion
            || await _context.AgentTestRuns.AsNoTracking().AnyAsync(item => item.AgentDefinitionId == id
                && item.AgentVersion == targetVersion && item.Status == AgentTestRunStatus.Passed, cancellationToken);
        if (!wasTested) throw new InvalidOperationException("目标版本没有通过记录，不能一键回滚");

        var restored = AgentDefinitionSnapshotService.Restore(entity, targetVersion, snapshot.SnapshotJson);
        CopyConfiguration(restored, entity);
        foreach (var currentTool in entity.ToolPermissions)
        {
            var restoredTool = restored.ToolPermissions.FirstOrDefault(item => string.Equals(item.ToolName, currentTool.ToolName, StringComparison.OrdinalIgnoreCase));
            currentTool.IsEnabled = restoredTool?.IsEnabled == true;
            currentTool.ReviewMode = restoredTool?.ReviewMode ?? currentTool.ReviewMode;
            currentTool.RequiresApproval = currentTool.ReviewMode == AgentToolReviewMode.HumanApproval;
            currentTool.UpdatedAt = AppTime.Now;
        }
        RestoreAcceptanceContract(entity, snapshot.SnapshotJson);
        entity.Version++;
        entity.StableVersion = entity.Version;
        entity.CanaryVersion = null;
        entity.CanaryPercent = 0;
        entity.DeploymentStatus = AgentDeploymentStatus.Stable;
        entity.LifecycleStatus = AgentLifecycleStatus.Published;
        entity.IsEnabled = true;
        entity.PublicationGatePassed = true;
        entity.LastTestStatus = AgentTestRunStatus.Passed;
        entity.UpdatedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
        await SaveVersionSnapshotAsync(entity.Id, changedByUserId, cancellationToken);
    }

    public async Task PauseAsync(int id, int? changedByUserId = null, CancellationToken cancellationToken = default)
    {
        var entity = await _context.AgentDefinitions.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Agent 不存在");
        if (entity.LifecycleStatus == AgentLifecycleStatus.Archived)
            throw new InvalidOperationException("已归档 Agent 不能暂停");
        entity.LifecycleStatus = AgentLifecycleStatus.Paused;
        entity.IsEnabled = false;
        entity.UpdatedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task ArchiveAsync(int id, int? changedByUserId = null, CancellationToken cancellationToken = default)
    {
        var entity = await _context.AgentDefinitions.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Agent 不存在");
        var hasActiveWork = await _context.AgentWorkItems.AsNoTracking().AnyAsync(item =>
            item.AgentDefinitionId == id
            && item.Status != AgentWorkItemStatus.Completed
            && item.Status != AgentWorkItemStatus.Failed
            && item.Status != AgentWorkItemStatus.Cancelled, cancellationToken);
        if (hasActiveWork) throw new InvalidOperationException("Agent 仍有活动工作项，请先暂停或取消后再归档");
        entity.LifecycleStatus = AgentLifecycleStatus.Archived;
        entity.IsEnabled = false;
        entity.CanReceiveTaskDispatch = false;
        entity.UpdatedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public static IReadOnlyList<string> GetPublicationReadinessErrors(AgentDefinition agent)
    {
        var errors = new List<string>();
        if (!agent.PublicationGatePassed) errors.Add("当前配置尚未通过隔离测试");
        if (agent.AcceptanceContract == null) errors.Add("尚未配置验收合同");
        else errors.AddRange(ValidateAcceptanceContract(agent.AcceptanceContract));
        return errors;
    }

    public static IReadOnlySet<AgentContextSource> ParseContextSources(string? json)
    {
        try
        {
            var names = JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? [];
            return names
                .Select(name => Enum.TryParse<AgentContextSource>(name, true, out var source)
                    ? (AgentContextSource?)source
                    : null)
                .Where(source => source.HasValue)
                .Select(source => source!.Value)
                .ToHashSet();
        }
        catch (JsonException)
        {
            return new HashSet<AgentContextSource>();
        }
    }

    public static IReadOnlyList<string> ParseCapabilities(string? json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? [])
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task SaveToolPermissionsAsync(
        AgentDefinition agent,
        IEnumerable<string> enabledTools,
        IEnumerable<string> approvalTools,
        CancellationToken cancellationToken)
    {
        var enabled = enabledTools.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var approval = approvalTools.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = await _context.AgentToolPermissions
            .Where(item => item.AgentDefinitionId == agent.Id)
            .ToListAsync(cancellationToken);

        foreach (var descriptor in _toolCatalog.GetAll())
        {
            var permission = existing.FirstOrDefault(item =>
                string.Equals(item.ToolName, descriptor.ToolName, StringComparison.OrdinalIgnoreCase));
            if (permission == null)
            {
                permission = new AgentToolPermission
                {
                    AgentDefinitionId = agent.Id,
                    ToolName = descriptor.ToolName
                };
                _context.AgentToolPermissions.Add(permission);
            }

            permission.IsEnabled = enabled.Contains(descriptor.ToolName);
            permission.ReviewMode = descriptor.MinimumReviewMode == AgentToolReviewMode.HumanApproval
                || descriptor.SupportsApproval && approval.Contains(descriptor.ToolName)
                    ? AgentToolReviewMode.HumanApproval
                    : descriptor.MinimumReviewMode;
            permission.RequiresApproval = permission.ReviewMode == AgentToolReviewMode.HumanApproval;
            permission.UpdatedAt = AppTime.Now;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveAcceptanceContractAsync(
        AgentDefinition agent,
        AgentAcceptanceContract input,
        CancellationToken cancellationToken)
    {
        var validationErrors = ValidateAcceptanceContract(input);
        if (validationErrors.Count > 0)
            throw new InvalidOperationException(string.Join("；", validationErrors));

        var contract = agent.AcceptanceContract
            ?? await _context.AgentAcceptanceContracts
                .FirstOrDefaultAsync(item => item.AgentDefinitionId == agent.Id, cancellationToken);
        if (contract == null)
        {
            contract = new AgentAcceptanceContract { AgentDefinitionId = agent.Id };
            _context.AgentAcceptanceContracts.Add(contract);
        }

        contract.Objective = input.Objective.Trim();
        contract.InputRequirements = input.InputRequirements.Trim();
        contract.RequiredOutput = input.RequiredOutput.Trim();
        contract.SuccessCriteria = input.SuccessCriteria.Trim();
        contract.ProhibitedActions = input.ProhibitedActions.Trim();
        contract.TestPrompt = input.TestPrompt.Trim();
        contract.ExpectedOutputTerms = NormalizeTerms(input.ExpectedOutputTerms);
        contract.ForbiddenOutputTerms = NormalizeTerms(input.ForbiddenOutputTerms);
        contract.UpdatedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public static IReadOnlyList<string> ValidateAcceptanceContract(AgentAcceptanceContract contract)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(contract.Objective)) errors.Add("验收合同缺少目标");
        if (string.IsNullOrWhiteSpace(contract.InputRequirements)) errors.Add("验收合同缺少输入要求");
        if (string.IsNullOrWhiteSpace(contract.RequiredOutput)) errors.Add("验收合同缺少必需输出");
        if (string.IsNullOrWhiteSpace(contract.SuccessCriteria)) errors.Add("验收合同缺少成功标准");
        if (string.IsNullOrWhiteSpace(contract.ProhibitedActions)) errors.Add("验收合同缺少禁止行为");
        if (string.IsNullOrWhiteSpace(contract.TestPrompt)) errors.Add("验收合同缺少测试任务");
        if (SplitTerms(contract.ExpectedOutputTerms).Count == 0) errors.Add("至少配置一个输出关键词");
        if (contract.Objective?.Length > 4000) errors.Add("验收目标不能超过 4000 个字符");
        if (contract.TestPrompt?.Length > 10000) errors.Add("测试任务不能超过 10000 个字符");
        if (NormalizeTerms(contract.ExpectedOutputTerms).Length > 2000) errors.Add("输出关键词总长度不能超过 2000 个字符");
        if (NormalizeTerms(contract.ForbiddenOutputTerms).Length > 2000) errors.Add("禁用内容总长度不能超过 2000 个字符");
        return errors;
    }

    public static IReadOnlyList<string> SplitTerms(string? value)
        => (value ?? string.Empty)
            .Replace('\r', '\n')
            .Split(['\n', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string NormalizeTerms(string? value) => string.Join('\n', SplitTerms(value));

    private static string SerializeContextSources(IEnumerable<AgentContextSource> sources)
    {
        return JsonSerializer.Serialize(sources.Distinct().OrderBy(item => item).Select(item => item.ToString()));
    }

    private async Task SaveVersionSnapshotAsync(int agentId, int? changedByUserId, CancellationToken cancellationToken)
    {
        var agent = await _context.AgentDefinitions.AsNoTracking()
            .Include(item => item.ToolPermissions)
            .Include(item => item.AcceptanceContract)
            .FirstAsync(item => item.Id == agentId, cancellationToken);
        var snapshot = JsonSerializer.Serialize(new
        {
            schemaVersion = 3,
            agent.AgentKey,
            agent.Name,
            agent.Description,
            agent.SystemPrompt,
            agent.ModelName,
            agent.Temperature,
            agent.MaxTokens,
            agent.MaxTurns,
            agent.TimeoutSeconds,
            agent.RequiresProject,
            agent.RequiresTask,
            agent.AutoCommentOnCompletion,
            agent.CanReceiveTaskDispatch,
            agent.StableVersion,
            agent.CanaryVersion,
            agent.CanaryPercent,
            agent.DeploymentStatus,
            agent.IsSystemManaged,
            agent.ManagedDefinitionVersion,
            agent.AvailableManagedDefinitionVersion,
            agent.HasLocalOverrides,
            agent.TemplateKey,
            agent.LifecycleStatus,
            agent.PublicationGatePassed,
            agent.ContextSourcesJson,
            agent.CapabilitiesJson,
            agent.IsEnabled,
            acceptanceContract = agent.AcceptanceContract == null ? null : new
            {
                agent.AcceptanceContract.Objective,
                agent.AcceptanceContract.InputRequirements,
                agent.AcceptanceContract.RequiredOutput,
                agent.AcceptanceContract.SuccessCriteria,
                agent.AcceptanceContract.ProhibitedActions,
                agent.AcceptanceContract.TestPrompt,
                agent.AcceptanceContract.ExpectedOutputTerms,
                agent.AcceptanceContract.ForbiddenOutputTerms
            },
            tools = agent.ToolPermissions.OrderBy(item => item.ToolName).Select(item => new
            {
                item.ToolName,
                item.IsEnabled,
                item.ReviewMode
            })
        });
        var existing = await _context.AgentDefinitionVersions
            .FirstOrDefaultAsync(item => item.AgentDefinitionId == agent.Id && item.Version == agent.Version, cancellationToken);
        if (existing == null)
        {
            _context.AgentDefinitionVersions.Add(new AgentDefinitionVersion
            {
                AgentDefinitionId = agent.Id,
                Version = agent.Version,
                ChangedByUserId = changedByUserId,
                SnapshotJson = snapshot
            });
        }
        else
        {
            // 同一事务重试时保持唯一版本，同时让最终快照与实际配置一致。
            existing.ChangedByUserId ??= changedByUserId;
            existing.SnapshotJson = snapshot;
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static void CopyConfiguration(AgentDefinition source, AgentDefinition target)
    {
        target.Name = source.Name;
        target.Description = source.Description;
        target.SystemPrompt = source.SystemPrompt;
        target.ModelName = source.ModelName;
        target.Temperature = source.Temperature;
        target.MaxTokens = source.MaxTokens;
        target.MaxTurns = source.MaxTurns;
        target.TimeoutSeconds = source.TimeoutSeconds;
        target.RequiresProject = source.RequiresProject;
        target.RequiresTask = source.RequiresTask;
        target.AutoCommentOnCompletion = source.AutoCommentOnCompletion;
        target.CanReceiveTaskDispatch = source.CanReceiveTaskDispatch;
        target.TemplateKey = source.TemplateKey;
        target.ContextSourcesJson = source.ContextSourcesJson;
        target.CapabilitiesJson = source.CapabilitiesJson;
    }

    private static void RestoreAcceptanceContract(AgentDefinition target, string snapshotJson)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        if (!document.RootElement.TryGetProperty("acceptanceContract", out var contract)
            || contract.ValueKind != JsonValueKind.Object || target.AcceptanceContract == null) return;
        target.AcceptanceContract.Objective = AgentDefinitionSnapshotService.ReadString(contract, nameof(AgentAcceptanceContract.Objective), target.AcceptanceContract.Objective);
        target.AcceptanceContract.InputRequirements = AgentDefinitionSnapshotService.ReadString(contract, nameof(AgentAcceptanceContract.InputRequirements), target.AcceptanceContract.InputRequirements);
        target.AcceptanceContract.RequiredOutput = AgentDefinitionSnapshotService.ReadString(contract, nameof(AgentAcceptanceContract.RequiredOutput), target.AcceptanceContract.RequiredOutput);
        target.AcceptanceContract.SuccessCriteria = AgentDefinitionSnapshotService.ReadString(contract, nameof(AgentAcceptanceContract.SuccessCriteria), target.AcceptanceContract.SuccessCriteria);
        target.AcceptanceContract.ProhibitedActions = AgentDefinitionSnapshotService.ReadString(contract, nameof(AgentAcceptanceContract.ProhibitedActions), target.AcceptanceContract.ProhibitedActions);
        target.AcceptanceContract.TestPrompt = AgentDefinitionSnapshotService.ReadString(contract, nameof(AgentAcceptanceContract.TestPrompt), target.AcceptanceContract.TestPrompt);
        target.AcceptanceContract.ExpectedOutputTerms = AgentDefinitionSnapshotService.ReadString(contract, nameof(AgentAcceptanceContract.ExpectedOutputTerms), target.AcceptanceContract.ExpectedOutputTerms);
        target.AcceptanceContract.ForbiddenOutputTerms = AgentDefinitionSnapshotService.ReadString(contract, nameof(AgentAcceptanceContract.ForbiddenOutputTerms), target.AcceptanceContract.ForbiddenOutputTerms);
        target.AcceptanceContract.UpdatedAt = AppTime.Now;
    }

    private static void NormalizeAndValidate(AgentDefinition input)
    {
        input.AgentKey = input.AgentKey?.Trim().ToLowerInvariant() ?? string.Empty;
        input.Name = input.Name?.Trim() ?? string.Empty;
        input.Description = input.Description?.Trim() ?? string.Empty;
        input.SystemPrompt = input.SystemPrompt?.Trim() ?? string.Empty;
        input.ModelName = input.ModelName?.Trim() ?? string.Empty;

        if (!KeyPattern.IsMatch(input.AgentKey))
            throw new InvalidOperationException("Agent Key 只能包含小写字母、数字和连字符，长度为 2–80");
        if (input.Name.Length is < 1 or > 120) throw new InvalidOperationException("Agent 名称长度必须为 1–120");
        if (input.Description.Length > 1000) throw new InvalidOperationException("Agent 说明不能超过 1000 个字符");
        if (input.SystemPrompt.Length is < 1 or > 20000) throw new InvalidOperationException("系统提示词长度必须为 1–20000");
        if (input.ModelName.Length > 100) throw new InvalidOperationException("模型名称不能超过 100 个字符");
        if (input.Temperature is < 0 or > 2) throw new InvalidOperationException("温度必须在 0–2 之间");
        if (input.MaxTokens is < 256 or > 32000) throw new InvalidOperationException("最大 Token 必须在 256–32000 之间");
        if (input.MaxTurns is < 1 or > 100) throw new InvalidOperationException("最大轮数必须在 1–100 之间");
        if (input.TimeoutSeconds is < 10 or > 600) throw new InvalidOperationException("超时时间必须在 10–600 秒之间");
    }
}

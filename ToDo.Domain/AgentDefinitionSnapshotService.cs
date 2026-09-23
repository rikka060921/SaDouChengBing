using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

/// <summary>把不可变版本快照还原为仅供执行的 Agent 定义，确保运行中的 Session 不漂移到新配置。</summary>
public sealed class AgentDefinitionSnapshotService
{
    private readonly ApplicationDbContext _context;

    public AgentDefinitionSnapshotService(ApplicationDbContext context) => _context = context;

    public async Task<AgentDefinition?> LoadRuntimeVersionAsync(
        AgentDefinition current,
        int version,
        CancellationToken cancellationToken = default)
    {
        if (version == current.Version) return current;
        var snapshotJson = await _context.AgentDefinitionVersions.AsNoTracking()
            .Where(item => item.AgentDefinitionId == current.Id && item.Version == version)
            .Select(item => item.SnapshotJson)
            .FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(snapshotJson) ? null : Restore(current, version, snapshotJson);
    }

    public static AgentDefinition Restore(AgentDefinition current, int version, string snapshotJson)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        var root = document.RootElement;
        var restored = new AgentDefinition
        {
            Id = current.Id,
            AgentKey = ReadString(root, nameof(AgentDefinition.AgentKey), current.AgentKey),
            Name = ReadString(root, nameof(AgentDefinition.Name), current.Name),
            Description = ReadString(root, nameof(AgentDefinition.Description), current.Description),
            SystemPrompt = ReadString(root, nameof(AgentDefinition.SystemPrompt), current.SystemPrompt),
            ModelName = ReadString(root, nameof(AgentDefinition.ModelName), current.ModelName),
            Temperature = ReadDouble(root, nameof(AgentDefinition.Temperature), current.Temperature),
            MaxTokens = ReadInt(root, nameof(AgentDefinition.MaxTokens), current.MaxTokens),
            MaxTurns = ReadInt(root, nameof(AgentDefinition.MaxTurns), current.MaxTurns),
            TimeoutSeconds = ReadInt(root, nameof(AgentDefinition.TimeoutSeconds), current.TimeoutSeconds),
            RequiresProject = ReadBool(root, nameof(AgentDefinition.RequiresProject), current.RequiresProject),
            RequiresTask = ReadBool(root, nameof(AgentDefinition.RequiresTask), current.RequiresTask),
            AutoCommentOnCompletion = ReadBool(root, nameof(AgentDefinition.AutoCommentOnCompletion), current.AutoCommentOnCompletion),
            CanReceiveTaskDispatch = ReadBool(root, nameof(AgentDefinition.CanReceiveTaskDispatch), current.CanReceiveTaskDispatch),
            TemplateKey = ReadString(root, nameof(AgentDefinition.TemplateKey), current.TemplateKey),
            ContextSourcesJson = ReadString(root, nameof(AgentDefinition.ContextSourcesJson), current.ContextSourcesJson),
            CapabilitiesJson = ReadString(root, nameof(AgentDefinition.CapabilitiesJson), current.CapabilitiesJson),
            Version = version,
            StableVersion = current.StableVersion,
            CanaryVersion = current.CanaryVersion,
            CanaryPercent = current.CanaryPercent,
            DeploymentStatus = current.DeploymentStatus,
            LifecycleStatus = AgentLifecycleStatus.Published,
            PublicationGatePassed = true,
            IsEnabled = true,
            IsSystemManaged = current.IsSystemManaged,
            ManagedDefinitionVersion = current.ManagedDefinitionVersion,
            AvailableManagedDefinitionVersion = current.AvailableManagedDefinitionVersion,
            HasLocalOverrides = current.HasLocalOverrides,
            CreatedAt = current.CreatedAt,
            UpdatedAt = current.UpdatedAt
        };
        if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in tools.EnumerateArray())
            {
                var toolName = ReadString(tool, nameof(AgentToolPermission.ToolName), string.Empty);
                if (string.IsNullOrWhiteSpace(toolName)) continue;
                restored.ToolPermissions.Add(new AgentToolPermission
                {
                    AgentDefinitionId = current.Id,
                    ToolName = toolName,
                    IsEnabled = ReadBool(tool, nameof(AgentToolPermission.IsEnabled), false),
                    ReviewMode = (AgentToolReviewMode)ReadInt(tool, nameof(AgentToolPermission.ReviewMode), 0),
                    RequiresApproval = (AgentToolReviewMode)ReadInt(tool, nameof(AgentToolPermission.ReviewMode), 0) == AgentToolReviewMode.HumanApproval
                });
            }
        }
        if (root.TryGetProperty("acceptanceContract", out var contract) && contract.ValueKind == JsonValueKind.Object)
        {
            restored.AcceptanceContract = new AgentAcceptanceContract
            {
                AgentDefinitionId = current.Id,
                Objective = ReadString(contract, "Objective", string.Empty),
                InputRequirements = ReadString(contract, "InputRequirements", string.Empty),
                RequiredOutput = ReadString(contract, "RequiredOutput", string.Empty),
                SuccessCriteria = ReadString(contract, "SuccessCriteria", string.Empty),
                ProhibitedActions = ReadString(contract, "ProhibitedActions", string.Empty),
                TestPrompt = ReadString(contract, "TestPrompt", string.Empty),
                ExpectedOutputTerms = ReadString(contract, "ExpectedOutputTerms", string.Empty),
                ForbiddenOutputTerms = ReadString(contract, "ForbiddenOutputTerms", string.Empty)
            };
        }
        return restored;
    }

    internal static string ReadString(JsonElement element, string name, string fallback)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback : fallback;
    internal static int ReadInt(JsonElement element, string name, int fallback)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
    internal static double ReadDouble(JsonElement element, string name, double fallback)
        => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : fallback;
    internal static bool ReadBool(JsonElement element, string name, bool fallback)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : fallback;
}

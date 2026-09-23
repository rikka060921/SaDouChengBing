using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum AgentDeploymentStatus
{
    Stable = 0,
    Canary = 1
}

[Table("agent_definitions")]
public class AgentDefinition
{
    public int Id { get; set; }

    [Required, MaxLength(80)]
    public string AgentKey { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string CapabilitiesJson { get; set; } = "[]";

    [Column(TypeName = "text")]
    public string SystemPrompt { get; set; } = string.Empty;

    [MaxLength(100)]
    public string ModelName { get; set; } = string.Empty;

    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 6000;
    public int MaxTurns { get; set; } = 20;
    public int TimeoutSeconds { get; set; } = 90;
    public bool RequiresProject { get; set; }
    public bool RequiresTask { get; set; }
    public bool AutoCommentOnCompletion { get; set; }
    /// <summary>是否允许调度 Agent 自动把任务分配给该 Agent。</summary>
    public bool CanReceiveTaskDispatch { get; set; } = true;
    public int Version { get; set; } = 1;
    /// <summary>当前承接稳定流量的不可变版本。</summary>
    public int StableVersion { get; set; }
    public int? CanaryVersion { get; set; }
    public int CanaryPercent { get; set; }
    public AgentDeploymentStatus DeploymentStatus { get; set; } = AgentDeploymentStatus.Stable;

    /// <summary>系统内置 Agent 的定义包版本；与业务运行 Version 分开。</summary>
    public bool IsSystemManaged { get; set; }
    public int ManagedDefinitionVersion { get; set; }
    public int AvailableManagedDefinitionVersion { get; set; }
    public bool HasLocalOverrides { get; set; }

    [MaxLength(64)]
    public string TemplateKey { get; set; } = string.Empty;

    public AgentLifecycleStatus LifecycleStatus { get; set; } = AgentLifecycleStatus.Draft;
    public bool PublicationGatePassed { get; set; }
    public AgentTestRunStatus LastTestStatus { get; set; } = AgentTestRunStatus.NotRun;
    public DateTime? LastTestAt { get; set; }
    public int? LastTestSessionId { get; set; }

    [MaxLength(1000)]
    public string ContextSourcesJson { get; set; } = "[]";

    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; } = AppTime.Now;
    public DateTime UpdatedAt { get; set; } = AppTime.Now;

    public ICollection<AgentToolPermission> ToolPermissions { get; set; } = new List<AgentToolPermission>();
    public ICollection<ToDoTask> AssignedTasks { get; set; } = new List<ToDoTask>();
    public ICollection<AgentWorkItem> WorkItems { get; set; } = new List<AgentWorkItem>();
    public ICollection<AgentDispatchDecision> DispatchRecommendations { get; set; } = new List<AgentDispatchDecision>();
    public ICollection<AgentDefinitionVersion> Versions { get; set; } = new List<AgentDefinitionVersion>();
    public AgentAcceptanceContract? AcceptanceContract { get; set; }
    public ICollection<AgentTestRun> TestRuns { get; set; } = new List<AgentTestRun>();
}

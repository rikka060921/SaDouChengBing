using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum AgentLifecycleStatus
{
    Draft = 0,
    Testing = 1,
    Published = 2,
    Paused = 3,
    Archived = 4
}

public enum AgentTestRunStatus
{
    NotRun = 0,
    Running = 1,
    Passed = 2,
    Failed = 3
}

public static class AgentLifecycleStatusExtensions
{
    public static string GetDisplayName(this AgentLifecycleStatus status) => status switch
    {
        AgentLifecycleStatus.Draft => "草稿",
        AgentLifecycleStatus.Testing => "测试中",
        AgentLifecycleStatus.Published => "已发布",
        AgentLifecycleStatus.Paused => "已暂停",
        AgentLifecycleStatus.Archived => "已归档",
        _ => status.ToString()
    };

    public static string GetDisplayName(this AgentTestRunStatus status) => status switch
    {
        AgentTestRunStatus.NotRun => "未测试",
        AgentTestRunStatus.Running => "测试中",
        AgentTestRunStatus.Passed => "已通过",
        AgentTestRunStatus.Failed => "未通过",
        _ => status.ToString()
    };
}

/// <summary>Agent 的发布验收合同。配置修改后必须重新通过隔离测试才能发布。</summary>
[Table("agent_acceptance_contracts")]
public sealed class AgentAcceptanceContract
{
    public int Id { get; set; }
    public int AgentDefinitionId { get; set; }

    [Column(TypeName = "text")]
    public string Objective { get; set; } = string.Empty;

    [Column(TypeName = "text")]
    public string InputRequirements { get; set; } = string.Empty;

    [Column(TypeName = "text")]
    public string RequiredOutput { get; set; } = string.Empty;

    [Column(TypeName = "text")]
    public string SuccessCriteria { get; set; } = string.Empty;

    [Column(TypeName = "text")]
    public string ProhibitedActions { get; set; } = string.Empty;

    [Column(TypeName = "text")]
    public string TestPrompt { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string ExpectedOutputTerms { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string ForbiddenOutputTerms { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = AppTime.Now;

    [ForeignKey(nameof(AgentDefinitionId))]
    public AgentDefinition? AgentDefinition { get; set; }
}

/// <summary>不开放业务工具的隔离测试记录，用于证明当前 Agent 配置具备可发布条件。</summary>
[Table("agent_test_runs")]
public sealed class AgentTestRun
{
    public long Id { get; set; }
    public int AgentDefinitionId { get; set; }
    public int AgentVersion { get; set; }
    public int RequestedByUserId { get; set; }
    public int? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public int? AiSessionId { get; set; }

    [Column(TypeName = "text")]
    public string Prompt { get; set; } = string.Empty;

    [Column(TypeName = "longtext")]
    public string ResultSummary { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string ValidationSummary { get; set; } = string.Empty;

    public AgentTestRunStatus Status { get; set; } = AgentTestRunStatus.Running;
    public DateTime StartedAt { get; set; } = AppTime.Now;
    public DateTime? CompletedAt { get; set; }

    [ForeignKey(nameof(AgentDefinitionId))]
    public AgentDefinition? AgentDefinition { get; set; }

    [ForeignKey(nameof(RequestedByUserId))]
    public ApplicationUser? RequestedByUser { get; set; }

    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }

    [ForeignKey(nameof(TaskId))]
    public ToDoTask? Task { get; set; }

    [ForeignKey(nameof(AiSessionId))]
    public AiSession? AiSession { get; set; }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum AgentDispatchDecisionStatus
{
    PendingConfirmation = 0,
    AutoAssigned = 1,
    Confirmed = 2,
    Superseded = 3,
    NoCandidate = 4
}

/// <summary>调度 Agent 对一次任务分配所做的可审计决策。</summary>
[Table("agent_dispatch_decisions")]
public class AgentDispatchDecision
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int ProjectId { get; set; }
    public int RequestedByUserId { get; set; }
    public int DispatchVersion { get; set; }
    public int? RecommendedAgentDefinitionId { get; set; }
    public int? SelectedAgentDefinitionId { get; set; }
    public AgentDispatchDecisionStatus Status { get; set; }
    public double Confidence { get; set; }

    [Column(TypeName = "text")]
    public string CandidatesJson { get; set; } = "[]";

    [MaxLength(2000)]
    public string Explanation { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = AppTime.Now;
    public DateTime UpdatedAt { get; set; } = AppTime.Now;
    public DateTime? ResolvedAt { get; set; }

    [ForeignKey(nameof(TaskId))]
    public ToDoTask? Task { get; set; }

    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }

    [ForeignKey(nameof(RequestedByUserId))]
    public ApplicationUser? RequestedByUser { get; set; }

    [ForeignKey(nameof(RecommendedAgentDefinitionId))]
    public AgentDefinition? RecommendedAgentDefinition { get; set; }

    [ForeignKey(nameof(SelectedAgentDefinitionId))]
    public AgentDefinition? SelectedAgentDefinition { get; set; }
}

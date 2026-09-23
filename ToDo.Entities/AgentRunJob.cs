using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum AgentRunJobStatus
{
    Pending = 0,
    Running = 1,
    WaitingApproval = 2,
    Retrying = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6
}

[Table("agent_run_jobs")]
public sealed class AgentRunJob
{
    public long Id { get; set; }
    public int AiSessionId { get; set; }
    public int AgentDefinitionId { get; set; }
    public int RequestedByUserId { get; set; }
    public int? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public long? DeliveryReceiptId { get; set; }
    public int? WaitingApprovalRequestId { get; set; }
    public AgentRunJobStatus Status { get; set; } = AgentRunJobStatus.Pending;

    [Column(TypeName = "text")]
    public string Prompt { get; set; } = string.Empty;

    [Column(TypeName = "text")]
    public string ResultSummary { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string ErrorMessage { get; set; } = string.Empty;

    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public int StepCount { get; set; }
    public int MaxSteps { get; set; } = 5;
    public DateTime NextRunAt { get; set; } = AppTime.Now;
    public DateTime CreatedAt { get; set; } = AppTime.Now;
    public DateTime UpdatedAt { get; set; } = AppTime.Now;
    public DateTime? LockedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    [ForeignKey(nameof(AiSessionId))]
    public AiSession? AiSession { get; set; }
    [ForeignKey(nameof(AgentDefinitionId))]
    public AgentDefinition? AgentDefinition { get; set; }
    [ForeignKey(nameof(RequestedByUserId))]
    public ApplicationUser? RequestedByUser { get; set; }
    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }
    [ForeignKey(nameof(TaskId))]
    public ToDoTask? Task { get; set; }
    [ForeignKey(nameof(DeliveryReceiptId))]
    public AgentDeliveryReceipt? DeliveryReceipt { get; set; }
    [ForeignKey(nameof(WaitingApprovalRequestId))]
    public ApprovalRequest? WaitingApprovalRequest { get; set; }
}

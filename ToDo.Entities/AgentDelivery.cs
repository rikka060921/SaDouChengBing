using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum AgentDeliveryAcceptanceStatus
{
    PendingReview = 0,
    Accepted = 1,
    Rejected = 2,
    Superseded = 3,
    AutomaticallyAccepted = 4
}

public enum AgentEvidenceQuality
{
    Basic = 0,
    Strong = 1
}

public enum AgentPerformanceEventType
{
    WorkSubmitted = 0,
    WorkFailed = 1,
    HumanAccepted = 2,
    HumanRejected = 3,
    DispatchRecommendationConfirmed = 4,
    DispatchRecommendationOverridden = 5,
    HumanSelected = 6,
    AutomaticallyAccepted = 7
}

/// <summary>
/// Agent 的一次正式交付。证据与工具影响均由服务端根据业务记录生成，不采信模型自述。
/// </summary>
[Table("agent_delivery_receipts")]
public sealed class AgentDeliveryReceipt
{
    public long Id { get; set; }
    public int AgentWorkItemId { get; set; }
    public int AgentDefinitionId { get; set; }
    public int ProjectId { get; set; }
    public int TaskId { get; set; }
    public int AiSessionId { get; set; }
    public int AgentVersion { get; set; }

    [Column(TypeName = "longtext")]
    public string OutcomeSummary { get; set; } = string.Empty;

    [Column(TypeName = "longtext")]
    public string EvidenceJson { get; set; } = "[]";

    [Column(TypeName = "longtext")]
    public string ToolEffectsJson { get; set; } = "[]";

    [MaxLength(2000)]
    public string ValidationSummary { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string RiskSummary { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string ContentHash { get; set; } = string.Empty;

    [MaxLength(128)]
    public string ContentSignature { get; set; } = string.Empty;

    [MaxLength(40)]
    public string SignatureKeyId { get; set; } = string.Empty;

    public AgentEvidenceQuality EvidenceQuality { get; set; } = AgentEvidenceQuality.Basic;
    public AgentDeliveryAcceptanceStatus AcceptanceStatus { get; set; } = AgentDeliveryAcceptanceStatus.PendingReview;
    public int? ReviewedByUserId { get; set; }

    [MaxLength(2000)]
    public string ReviewComment { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = AppTime.Now;
    public DateTime UpdatedAt { get; set; } = AppTime.Now;
    public DateTime? ReviewedAt { get; set; }

    [ForeignKey(nameof(AgentWorkItemId))]
    public AgentWorkItem? AgentWorkItem { get; set; }

    [ForeignKey(nameof(AgentDefinitionId))]
    public AgentDefinition? AgentDefinition { get; set; }

    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }

    [ForeignKey(nameof(TaskId))]
    public ToDoTask? Task { get; set; }

    [ForeignKey(nameof(AiSessionId))]
    public AiSession? AiSession { get; set; }

    [ForeignKey(nameof(ReviewedByUserId))]
    public ApplicationUser? ReviewedByUser { get; set; }
}

/// <summary>调度与人工验收产生的结构化反馈，可用于后续派单评分并通过事件键保证幂等。</summary>
[Table("agent_performance_signals")]
public sealed class AgentPerformanceSignal
{
    public long Id { get; set; }
    public int AgentDefinitionId { get; set; }
    public int? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public int? AgentWorkItemId { get; set; }
    public long? DeliveryReceiptId { get; set; }
    public int? DispatchDecisionId { get; set; }
    public AgentPerformanceEventType EventType { get; set; }
    public double ScoreDelta { get; set; }

    [Required, MaxLength(160)]
    public string EventKey { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Reason { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = AppTime.Now;

    [ForeignKey(nameof(AgentDefinitionId))]
    public AgentDefinition? AgentDefinition { get; set; }

    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }

    [ForeignKey(nameof(TaskId))]
    public ToDoTask? Task { get; set; }

    [ForeignKey(nameof(AgentWorkItemId))]
    public AgentWorkItem? AgentWorkItem { get; set; }

    [ForeignKey(nameof(DeliveryReceiptId))]
    public AgentDeliveryReceipt? DeliveryReceipt { get; set; }

    [ForeignKey(nameof(DispatchDecisionId))]
    public AgentDispatchDecision? DispatchDecision { get; set; }
}

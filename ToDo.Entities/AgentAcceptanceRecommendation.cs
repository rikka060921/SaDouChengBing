using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum AgentAcceptanceRecommendationStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2
}

public enum AgentAcceptanceVerdict
{
    NeedsHumanReview = 0,
    RecommendAccept = 1,
    RecommendReject = 2
}

/// <summary>独立验收 Agent 针对一份不可变交付凭证给出的结构化建议；正式决定仍由人工作出。</summary>
[Table("agent_acceptance_recommendations")]
public sealed class AgentAcceptanceRecommendation
{
    public long Id { get; set; }
    public long DeliveryReceiptId { get; set; }
    public int AiSessionId { get; set; }
    public long AgentRunJobId { get; set; }
    public int RequestedByUserId { get; set; }
    public AgentAcceptanceRecommendationStatus Status { get; set; } = AgentAcceptanceRecommendationStatus.Pending;
    public AgentAcceptanceVerdict Verdict { get; set; } = AgentAcceptanceVerdict.NeedsHumanReview;
    public double Confidence { get; set; }

    [Column(TypeName = "longtext")]
    public string Summary { get; set; } = string.Empty;
    [Column(TypeName = "longtext")]
    public string EvidenceCoverageJson { get; set; } = "[]";
    [Column(TypeName = "longtext")]
    public string MissingItemsJson { get; set; } = "[]";
    [Column(TypeName = "longtext")]
    public string RisksJson { get; set; } = "[]";
    [Column(TypeName = "longtext")]
    public string HumanReviewQuestionsJson { get; set; } = "[]";
    [Column(TypeName = "longtext")]
    public string RawResponse { get; set; } = string.Empty;
    [MaxLength(2000)]
    public string ErrorMessage { get; set; } = string.Empty;
    [MaxLength(40)]
    public string ParseMode { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = AppTime.Now;
    public DateTime? CompletedAt { get; set; }

    [ForeignKey(nameof(DeliveryReceiptId))]
    public AgentDeliveryReceipt? DeliveryReceipt { get; set; }
    [ForeignKey(nameof(AgentRunJobId))]
    public AgentRunJob? AgentRunJob { get; set; }
    [ForeignKey(nameof(RequestedByUserId))]
    public ApplicationUser? RequestedByUser { get; set; }
}

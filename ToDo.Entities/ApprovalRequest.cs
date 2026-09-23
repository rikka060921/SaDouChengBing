using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum ApprovalRequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Failed = 3,
    Processing = 4
}

[Table("approval_requests")]
public class ApprovalRequest
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int RequestedById { get; set; }
    public int? ReviewedById { get; set; }
    public int? SourceId { get; set; }

    [Required, MaxLength(80)]
    public string SourceType { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string ActionType { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Summary { get; set; } = string.Empty;

    [Column(TypeName = "longtext")]
    public string PayloadJson { get; set; } = "{}";

    /// <summary>项目、申请人、来源、动作、风险和载荷的 SHA-256，用于执行前检测意外篡改。</summary>
    [MaxLength(64)]
    public string PayloadHash { get; set; } = string.Empty;

    [MaxLength(128)]
    public string PayloadSignature { get; set; } = string.Empty;

    [MaxLength(40)]
    public string SignatureKeyId { get; set; } = string.Empty;

    public AgentRiskLevel RiskLevel { get; set; } = AgentRiskLevel.High;
    public AgentToolReviewMode ReviewMode { get; set; } = AgentToolReviewMode.HumanApproval;

    [MaxLength(100)]
    public string PolicyName { get; set; } = string.Empty;

    [Column(TypeName = "text")]
    public string AutomatedReviewJson { get; set; } = string.Empty;

    public ApprovalRequestStatus Status { get; set; } = ApprovalRequestStatus.Pending;
    public DateTime RequestedAt { get; set; } = AppTime.Now;
    public DateTime? ReviewedAt { get; set; }

    [MaxLength(1000)]
    public string ReviewComment { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string ExecutionResult { get; set; } = string.Empty;

    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }

    [ForeignKey(nameof(RequestedById))]
    public ApplicationUser? RequestedBy { get; set; }

    [ForeignKey(nameof(ReviewedById))]
    public ApplicationUser? ReviewedBy { get; set; }
}

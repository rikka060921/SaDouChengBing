using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum AiSessionStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    WaitingHuman = 3,
    Cancelled = 4
}

[Table("ai_sessions")]
public class AiSession
{
    public int Id { get; set; }

    [Required, MaxLength(80)]
    public string SessionKey { get; set; } = Guid.NewGuid().ToString("N");

    [Required, MaxLength(80)]
    public string AgentKey { get; set; } = string.Empty;

    public int? UserId { get; set; }
    public int? ProjectId { get; set; }
    public int? TaskId { get; set; }
    /// <summary>本次运行明确审查的交付凭证。为空时才允许读取任务的最新交付。</summary>
    public long? ContextDeliveryReceiptId { get; set; }

    [Required]
    public AiSessionStatus Status { get; set; } = AiSessionStatus.Running;

    public string Prompt { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;

    [MaxLength(100)]
    public string ModelName { get; set; } = string.Empty;

    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public long DurationMilliseconds { get; set; }
    [Column(TypeName = "decimal(18,6)")]
    public decimal? EstimatedCost { get; set; }
    public int AgentVersion { get; set; } = 1;
    public long ConcurrencyVersion { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public int TurnCount { get; set; }
    public DateTime StartedAt { get; set; } = AppTime.Now;
    public DateTime LastActivityAt { get; set; } = AppTime.Now;
    public DateTime? CompletedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }

    public ICollection<AiSessionMessage> Messages { get; set; } = new List<AiSessionMessage>();
    public ICollection<AgentToolCall> ToolCalls { get; set; } = new List<AgentToolCall>();
}

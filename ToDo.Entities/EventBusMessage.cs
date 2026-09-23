using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum EventBusMessageStatus
{
    Pending = 0,
    Processing = 1,
    Processed = 2,
    Failed = 3
}

[Table("event_bus_messages")]
public class EventBusMessage
{
    public long Id { get; set; }

    [Required, MaxLength(120)]
    public string EventType { get; set; } = string.Empty;

    [MaxLength(120)]
    public string AggregateType { get; set; } = string.Empty;

    [MaxLength(80)]
    public string AggregateId { get; set; } = string.Empty;

    [Required]
    public string PayloadJson { get; set; } = "{}";

    public EventBusMessageStatus Status { get; set; } = EventBusMessageStatus.Pending;
    public int RetryCount { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = AppTime.Now;
    public DateTime? LockedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}

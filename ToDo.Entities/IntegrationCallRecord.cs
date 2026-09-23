using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

[Table("integration_call_records")]
public class IntegrationCallRecord
{
    public int Id { get; set; }

    [Required, MaxLength(40)]
    public string Provider { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Operation { get; set; } = string.Empty;

    [MaxLength(30)]
    public string Status { get; set; } = "NotConfigured";

    [MaxLength(64)]
    public string CorrelationId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [MaxLength(50)]
    public string ErrorCategory { get; set; } = string.Empty;

    public string RequestJson { get; set; } = "{}";
    public string ResponseJson { get; set; } = "{}";
    public bool IsSuccess { get; set; }
    public int AttemptCount { get; set; }
    public int DurationMs { get; set; }
    public int? HttpStatusCode { get; set; }
    public DateTime CreatedAt { get; set; } = AppTime.Now;
}

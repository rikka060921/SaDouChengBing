using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

/// <summary>跨应用实例的短期后台任务租约，避免同一周期任务被重复执行。</summary>
[Table("background_job_leases")]
public sealed class BackgroundJobLease
{
    [Key, MaxLength(120)]
    public string LeaseKey { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string OwnerId { get; set; } = string.Empty;

    public DateTime AcquiredAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

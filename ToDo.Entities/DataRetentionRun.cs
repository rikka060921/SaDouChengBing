using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum DataRetentionRunStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Skipped = 3
}

/// <summary>一次数据留存任务的不可变执行摘要，用于审计谁在何时归档或清理了多少数据。</summary>
[Table("data_retention_runs")]
public sealed class DataRetentionRun
{
    public long Id { get; set; }
    public int? TriggeredByUserId { get; set; }

    [Required, MaxLength(30)]
    public string Trigger { get; set; } = "manual";

    public DataRetentionRunStatus Status { get; set; } = DataRetentionRunStatus.Running;
    public DateTime ArchiveSessionsBefore { get; set; }
    public DateTime DeleteNotificationsBefore { get; set; }
    public int ArchivedSessionCount { get; set; }
    public int DeletedNotificationCount { get; set; }

    [MaxLength(2000)]
    public string ErrorMessage { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; } = AppTime.Now;
    public DateTime? CompletedAt { get; set; }

    [ForeignKey(nameof(TriggeredByUserId))]
    public ApplicationUser? TriggeredByUser { get; set; }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum ProjectActivityEntityType
{
    Task = 0,
    MeetingMinutes = 1,
    ProjectDocument = 2,
    Project = 3
}

public enum ProjectActivityChangeType
{
    Created = 0,
    Updated = 1,
    Deleted = 2,
    Restored = 3
}

/// <summary>
/// 项目内的结构化变更记录。自动日报以此为增量数据源，避免仅凭 UpdatedAt 猜测变更内容。
/// </summary>
[Table("project_activity_records")]
public class ProjectActivityRecord
{
    public long Id { get; set; }
    public int ProjectId { get; set; }
    public ProjectActivityEntityType EntityType { get; set; }
    public int? EntityId { get; set; }
    public ProjectActivityChangeType ChangeType { get; set; }

    [Required, MaxLength(80)]
    public string FieldName { get; set; } = string.Empty;

    [Required, MaxLength(255)]
    public string EntityName { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? OldValue { get; set; }

    [MaxLength(2000)]
    public string? NewValue { get; set; }

    public DateTime OccurredAt { get; set; } = AppTime.Now;

    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }
}

/// <summary>
/// 每个项目自动汇总成功后的水位。仅在日报与水位同时保存成功后前移。
/// </summary>
[Table("project_summary_checkpoints")]
public class ProjectSummaryCheckpoint
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public DateTime LastSuccessfulSummaryAt { get; set; }
    public int? LastDailyReportId { get; set; }
    public DateTime UpdatedAt { get; set; } = AppTime.Now;

    /// <summary>乐观并发版本，阻止两个汇总任务重复消费同一段变更。</summary>
    public int Version { get; set; }

    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }

    [ForeignKey(nameof(LastDailyReportId))]
    public DailyReport? LastDailyReport { get; set; }
}

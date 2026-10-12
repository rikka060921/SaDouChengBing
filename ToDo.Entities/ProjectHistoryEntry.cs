using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

/// <summary>Retrospective milestones are separate from live activity and audit records.</summary>
[Table("project_history_entries")]
public sealed class ProjectHistoryEntry
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    [Required, MaxLength(80)] public string SourceKey { get; set; } = string.Empty;
    [Required, MaxLength(32)] public string Period { get; set; } = string.Empty;
    public DateTime SortDate { get; set; }
    [Required, MaxLength(160)] public string Title { get; set; } = string.Empty;
    [Required, MaxLength(2000)] public string Summary { get; set; } = string.Empty;
    [Required, MaxLength(1000)] public string Sources { get; set; } = string.Empty;
    public int ImportedByUserId { get; set; }
    public DateTime ImportedAt { get; set; } = AppTime.Now;
    public Project Project { get; set; } = null!;
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

/// <summary>Append-only personal additions/corrections. Never overwritten by an automatic summary rebuild.</summary>
[Table("daily_report_supplements")]
public sealed class DailyReportSupplement
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int UserId { get; set; }
    public DateTime ReportDate { get; set; }
    public int Revision { get; set; }
    [Required, MaxLength(12000)] public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = AppTime.Now;
    public Project Project { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
}

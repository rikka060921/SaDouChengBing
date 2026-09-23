using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum ScheduledJobType
{
    DailyReport = 0,
    PersonalDailySummary = 1,
    TeamDailySummary = 2
}

[Table("scheduled_jobs")]
public class ScheduledJob
{
    public int Id { get; set; }
    public int CreatedById { get; set; }

    [Required, MaxLength(80)]
    public string JobKey { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    public ScheduledJobType JobType { get; set; } = ScheduledJobType.DailyReport;
    public TimeSpan RunAt { get; set; } = new(18, 0, 0);
    public int? ProjectId { get; set; }
    public int? UserId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastRunAt { get; set; }
    public DateTime? NextRunAt { get; set; }
    public string LastResult { get; set; } = string.Empty;
    public bool IsRunning { get; set; }
    public DateTime? LockedAt { get; set; }
    [MaxLength(64)]
    public string LockToken { get; set; } = string.Empty;
    public int ConsecutiveFailureCount { get; set; }
    [MaxLength(2000)]
    public string LastError { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = AppTime.Now;
    public DateTime UpdatedAt { get; set; } = AppTime.Now;
}

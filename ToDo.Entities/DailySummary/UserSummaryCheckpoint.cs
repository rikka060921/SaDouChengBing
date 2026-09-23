using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ToDo.Entities;

namespace ToDo.Entities.DailySummary;

/// <summary>
/// 每位用户个人日报的成功汇总水位。AI 调用和个人日报保存成功后才前移。
/// </summary>
[Table("user_summary_checkpoints")]
public class UserSummaryCheckpoint
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public DateTime LastSuccessfulSummaryAt { get; set; }
    public int? LastDailyWorkSummaryId { get; set; }
    public DateTime UpdatedAt { get; set; } = AppTime.Now;

    /// <summary>乐观并发版本，避免同一用户的同一段变化被重复汇总。</summary>
    public int Version { get; set; }

    [ForeignKey(nameof(UserId))]
    public ApplicationUser? User { get; set; }

    [ForeignKey(nameof(LastDailyWorkSummaryId))]
    public DailyWorkSummary? LastDailyWorkSummary { get; set; }
}

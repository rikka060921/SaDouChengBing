using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ToDo.Entities;

namespace ToDo.Entities.DailySummary
{
    /// <summary>
    /// 每日工作情况汇总主表
    /// 存储AI生成的全局汇总数据，用于页面展示头部信息
    /// </summary>
    [Table("daily_work_summary")]
    public class DailyWorkSummary
    {
        /// <summary>
        /// 主键ID
        /// </summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// 汇总日期（yyyy-MM-dd）
        /// </summary>
        [Required]
        public DateTime SummaryDate { get; set; }

        /// <summary>
        /// 汇总所属用户。旧版全局汇总没有用户，因此允许为空；新生成的个人日报必须填写。
        /// </summary>
        public int? UserId { get; set; }

        /// <summary>
        /// 汇总标题（默认：yyyy-MM-dd 工作汇总汇报）
        /// </summary>
        [Required]
        [MaxLength(200)]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// AI生成时间
        /// </summary>
        [Required]
        public DateTime CreateTime { get; set; } = AppTime.Now;

        /// <summary>本次汇总实际覆盖的开始时间。</summary>
        public DateTime? SummaryStartAt { get; set; }

        /// <summary>本次汇总实际覆盖的截止时间。</summary>
        public DateTime? SummaryEndAt { get; set; }

        /// <summary>是否由定时任务自动生成。</summary>
        public bool IsAutomatic { get; set; }

        /// <summary>
        /// 整体工作总总结（AI生成）
        /// </summary>
        [Column(TypeName = "text")]
        public string TotalSummary { get; set; } = string.Empty;

        /// <summary>
        /// 汇总状态（0=待生成 1=已生成 2=生成失败）
        /// </summary>
        [Required]
        public int SummaryStatus { get; set; } = 0;

        /// <summary&gt;
        /// 逻辑删除标识
        /// </summary>
        public bool IsDeleted { get; set; } = false;

        [ForeignKey(nameof(UserId))]
        public ApplicationUser? User { get; set; }
    }
}

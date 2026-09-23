using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities.DailySummary
{
    /// <summary>
    /// 项目关联数据汇总子项
    /// 存储单个项目下任务、会议纪要、日报的AI总结内容
    /// </summary>
    [Table("project_relation_summary")]
    public class ProjectRelationSummary
    {
        /// <summary>
        /// 主键ID
        /// </summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// 关联项目汇总明细ID
        /// </summary>
        [Required]
        public int ProjectSummaryId { get; set; }

        /// <summary>
        /// 数据类型（Task=任务 Meeting=会议 Report=日报）
        /// </summary>
        [Required]
        [MaxLength(20)]
        public string DataType { get; set; } = string.Empty;

        /// <summary>
        /// 原始数据标题（任务标题/会议标题/日报标题）
        /// </summary>
        [MaxLength(200)]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// 关键属性（任务状态、负责人等，拼接展示）
        /// </summary>
        [MaxLength(500)]
        public string KeyAttributes { get; set; } = string.Empty;

        /// <summary>
        /// 内容总结（AI精简生成）
        /// </summary>
        [Column(TypeName = "text")]
        public string ContentSummary { get; set; } = string.Empty;

        /// <summary>
        /// 生成时间
        /// </summary>
        [Required]
        public DateTime CreateTime { get; set; } = AppTime.Now;

        /// <summary>
        /// 逻辑删除标识
        /// </summary>
        public bool IsDeleted { get; set; } = false;

        // 导航属性
        [ForeignKey(nameof(ProjectSummaryId))]
        public virtual DailyProjectSummaryDetail ProjectSummaryDetail { get; set; } = null!;
    }
}
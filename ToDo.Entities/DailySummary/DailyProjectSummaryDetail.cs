using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities.DailySummary
{
    /// <summary>
    /// 每日项目汇总明细
    /// 对应瀑布流每个项目展示模块，存储单项目AI汇总结果
    /// </summary>
    [Table("daily_project_summary_detail")]
    public class DailyProjectSummaryDetail
    {
        /// <summary>
        /// 主键ID
        /// </summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// 关联每日汇总主表ID
        /// </summary>
        [Required]
        public int DailySummaryId { get; set; }

        /// <summary>
        /// 关联项目ID（外键，对应Project.Id）
        /// </summary>
        [Required]
        public int ProjectId { get; set; }

        /// <summary>
        /// 项目名称
        /// </summary>
        [Required]
        [MaxLength(255)]
        public string ProjectName { get; set; } = string.Empty;

        /// <summary>
        /// 项目类型标记（今日新建/历史项目今日更新）
        /// </summary>
        [MaxLength(50)]
        public string ProjectTag { get; set; } = string.Empty;

        /// <summary>
        /// 项目负责人姓名
        /// </summary>
        [MaxLength(50)]
        public string LeaderUserName { get; set; } = string.Empty;

        /// <summary>
        /// 项目描述总结（AI精简生成）
        /// </summary>
        [Column(TypeName = "text")]
        public string ProjectDescSummary { get; set; } = string.Empty;

        /// <summary>
        /// 单项目小结（AI生成）
        /// </summary>
        [Column(TypeName = "text")]
        public string ProjectSummary { get; set; } = string.Empty;

        /// <summary>
        /// 数据生成时间
        /// </summary>
        [Required]
        public DateTime CreateTime { get; set; } = AppTime.Now;

        /// <summary>
        /// 逻辑删除标识
        /// </summary>
        public bool IsDeleted { get; set; } = false;

        // 导航属性
        [ForeignKey(nameof(DailySummaryId))]
        public virtual DailyWorkSummary DailyWorkSummary { get; set; } = null!;
        /// <summary>
        /// 导航属性：关联原始项目（用于权限判断）
        /// </summary>
        [ForeignKey(nameof(ProjectId))]
        public virtual Project Project { get; set; } = null!;
    }
}
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities
{
    public class DailyReport
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "必须选择项目")]
        public int ProjectId { get; set; }

        [Required(ErrorMessage = "必须选择报告类型")]
        [Range(1, 4, ErrorMessage = "请选择有效的报告类型")]
        public int ReportType { get; set; } // 1-日报，2-周报，3-月报，4-团队汇报

        [Required(ErrorMessage = "必须填写报告日期")]
        public DateTime ReportDate { get; set; }

        [Required(ErrorMessage = "报告标题不能为空")]
        [StringLength(200, ErrorMessage = "标题不能超过200个字符")]
        public string ReportTitle { get; set; } = string.Empty;

        [Required(ErrorMessage = "报告内容不能为空")]
        public string ReportContent { get; set; } = string.Empty;

        [Required(ErrorMessage = "必须指定上报人")]
        public int ReporterId { get; set; }
        /// <summary>
        /// 导航属性：日报附件集合（关键！关联日报附件）
        /// </summary>
        public ICollection<DailyReportAttachment> Attachments { get; set; } = new List<DailyReportAttachment>();

        public DateTime CreatedAt { get; set; } = AppTime.Now;
        public DateTime LastModifiedAt { get; set; } = AppTime.Now;
        /// <summary>由系统定时任务自动生成，不计入某个人的主动工作产出。</summary>
        public bool IsSystemGenerated { get; set; } = false;
        public bool IsDeleted { get; set; } = false;

        [ForeignKey("ProjectId")]
        public virtual Project Project { get; set; } = null!;

        [ForeignKey("ReporterId")]
        public virtual ApplicationUser Reporter { get; set; } = null!;

        // 获取报告类型文本
        public string GetReportTypeText()
        {
            return ReportType switch
            {
                1 => "日报",
                2 => "周报",
                3 => "月报",
                4 => "成员日报",
                _ => "未知"
            };
        }
    }
}

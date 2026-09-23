using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities
{
    /// <summary>
    /// 会议纪要来源类型
    /// </summary>
    public enum MeetingSourceType
    {
        /// <summary>
        /// 手动录入（默认值，历史旧数据兼容）
        /// </summary>
        Manual = 0,

        /// <summary>
        /// 腾讯会议API自动拉取转写生成
        /// </summary>
        TencentMeeting = 1,
               /// <summary>手动粘贴转写原文生成</summary>
    PastedTranscript = 2
    }

    public class MeetingMinutes
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "会议标题不能为空")]
        [StringLength(200, ErrorMessage = "标题不能超过200个字符")]
        public string MeetingTitle { get; set; } = string.Empty;

        [Required(ErrorMessage = "会议内容不能为空")]
        public string MeetingContent { get; set; } = string.Empty;

        public DateTime MeetingDate { get; set; }

        [Required]
        public int CreatorId { get; set; }

        /// <summary>
        /// 首项目ID（多项目会议时取第一个选中的项目）。
        /// 用于列表页筛选、面包屑、简报、权限判断等单项目场景。
        /// 完整的关联项目列表见 MeetingProjects 导航属性。
        /// </summary>
        [Required(ErrorMessage = "必须关联项目")]
        [Range(1, int.MaxValue, ErrorMessage = "请选择有效的项目")]
        public int ProjectId { get; set; }

        public DateTime CreatedAt { get; set; } = AppTime.Now;
        public DateTime LastModifiedAt { get; set; } = AppTime.Now;
        public bool IsDeleted { get; set; } = false;

        public bool IsDraft { get; set; }
        public DateTime? SubmittedAt { get; set; }

        /// <summary>会议确认时间（确认写入任务或确认无新任务后设置）</summary>
        public DateTime? ConfirmedAt { get; set; }

        [Column(TypeName = "longtext")]
        public string? AiSummary { get; set; }

        /// <summary>AI解析的开会原因</summary>
        [Column(TypeName = "longtext")]
        public string? AiMeetingPurpose { get; set; }

        /// <summary>AI解析的会议决策（JSON格式）</summary>
        [Column(TypeName = "longtext")]
        public string? AiDecisionsJson { get; set; }

        [Column(TypeName = "longtext")]
        public string? AiActionItemsJson { get; set; }

        [Column(TypeName = "longtext")]
        public string? TranscriptText { get; set; }

        #region 【新增：来源拓展字段 不改动原有任何字段】
        /// <summary>
        /// 纪要来源：0手动录入 / 1腾讯会议拉取
        /// </summary>
        [Column(TypeName = "tinyint")]
        public MeetingSourceType SourceType { get; set; } = MeetingSourceType.Manual;

        /// <summary>
        /// 手动录入原始内容（来源=Manual时使用）
        /// </summary>
        [Column(TypeName = "longtext")]
        public string ManualInputContent { get; set; } = string.Empty;

        /// <summary>
        /// 腾讯会议接口原始返回JSON完整备份（多场合并全部存储）
        /// </summary>
        [Column(TypeName = "longtext")]
        public string RawTranscriptJson { get; set; } = string.Empty;

        /// <summary>
        /// 清洗、合并、格式化后最终正文
        /// AI解析行动项统一读取此字段
        /// </summary>
        [Column(TypeName = "longtext")]
        public string CleanContent { get; set; } = string.Empty;

        /// <summary>本次拉取的腾讯会议record_file_id逗号分隔，用于追溯</summary>
        [StringLength(1000)]
        public string? TencentRecordFileIds { get; set; }
        #endregion

        /// <summary>关联的会前准备草稿ID（可空，支持从草稿创建会议纪要并锁定草稿）</summary>
        public int? PrepDraftId { get; set; }

        [ForeignKey("ProjectId")]
        public virtual Project Project { get; set; } = null!;

        public ICollection<MeetingAttachment> Attachments { get; set; } = new List<MeetingAttachment>();
        public ICollection<MeetingVersion> Versions { get; set; } = new List<MeetingVersion>();
        public ICollection<MeetingActionItem> ActionItems { get; set; } = new List<MeetingActionItem>();

        /// <summary>
        /// 本场会议关联的所有项目（多对多）。
        /// 其中 IsPrimary=true 的那条对应 ProjectId（首项目）。
        /// </summary>
        public ICollection<MeetingMinutesProject> MeetingProjects { get; set; }
            = new List<MeetingMinutesProject>();

        public ICollection<MeetingAgendaRelation> AgendaRelations { get; set; } = new List<MeetingAgendaRelation>();

        public void UpdateLastModified() => LastModifiedAt = AppTime.Now;
        public void MarkAsDeleted() { IsDeleted = true; UpdateLastModified(); }
    }
}

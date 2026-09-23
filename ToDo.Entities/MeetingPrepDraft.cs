namespace ToDo.Entities
{
    using System;
    using System.ComponentModel.DataAnnotations;
    using System.ComponentModel.DataAnnotations.Schema;
    using Microsoft.AspNetCore.Identity;

    /// <summary>
    /// 会前准备草稿状态。
    /// </summary>
    public enum MeetingPrepDraftStatus
    {
        /// <summary>草稿：会前可反复编辑修改</summary>
        Draft = 0,

        /// <summary>已定稿：会议已开，草稿锁定</summary>
        Finalized = 1
    }

    /// <summary>
    /// 会前准备草稿：保存选中的项目与任务，支持跨项目，会前可反复编辑。
    /// </summary>
    [Table("MeetingPrepDrafts")]
    public class MeetingPrepDraft
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "草稿标题不能为空")]
        [StringLength(200, ErrorMessage = "标题不能超过200个字符")]
        public string Title { get; set; } = string.Empty;

        [Required]
        public int CreatorId { get; set; }

        [ForeignKey("CreatorId")]
        public ApplicationUser? Creator { get; set; }

        public MeetingPrepDraftStatus Status { get; set; } = MeetingPrepDraftStatus.Draft;

        /// <summary>JSON int[]：勾选的项目ID（编辑态重载实时任务用）</summary>
        [Column(TypeName = "longtext")]
        [Required]
        public string SelectedProjectIdsJson { get; set; } = "[]";

        /// <summary>JSON int[]：勾选的任务ID（编辑态用）</summary>
        [Column(TypeName = "longtext")]
        [Required]
        public string SelectedTaskIdsJson { get; set; } = "[]";

        /// <summary>
        /// JSON [{id,title,status,projectName,assigneeName,deadline,isOverdue}]：
        /// 保存时生成的任务快照，列表页直接展示，不再 join 实时任务。
        /// </summary>
        [Column(TypeName = "longtext")]
        public string? TaskSnapshotJson { get; set; }

        public DateTime CreatedAt { get; set; } = AppTime.Now;

        public DateTime LastModifiedAt { get; set; } = AppTime.Now;

        public bool IsDeleted { get; set; }
    }
}

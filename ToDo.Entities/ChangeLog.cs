// Entities/ChangeLog.cs
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities
{
    public class ChangeLog
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public OperationType OperationType { get; set; }

        [Required]
        public OperationStatus OperationStatus { get; set; } = OperationStatus.成功;

        [Required]
        public DateTime OperatedAt { get; set; } = DateTime.UtcNow;

        [Required]
        public OperationTarget OperationTarget { get; set; }

        [Required]
        [ForeignKey("OperatedByUser")]
        public int OperatedByUserId { get; set; }

        [MaxLength(256)]
        public string? OperatedByUserName { get; set; }

        [MaxLength(50)]
        public string? OperatedByRealName { get; set; }

        // 项目相关
        public int? ProjectId { get; set; }
        public string? ProjectName { get; set; }

        // 任务相关
        public int? TaskId { get; set; }
        public string? TaskTitle { get; set; }

        // 其他对象
        public int? TargetId { get; set; }
        public string? TargetName { get; set; }

        [Column(TypeName = "text")]
        public string? BeforeContent { get; set; }

        [Column(TypeName = "text")]
        public string? AfterContent { get; set; }

        // 导航属性
        public ApplicationUser? OperatedByUser { get; set; }
        public Project? Project { get; set; }
        public ToDoTask? Task { get; set; }
       
        public string? TaskName { get; set; }
    }

    public enum OperationType
    {
        创建 = 1,
        更新 = 2,
        删除 = 3,
        启用 = 4,
        归档 = 5,
        恢复 = 6,
        状态变更 = 7,
        密码变更 = 8,
            禁用 =9

    }

    public enum OperationStatus
    {
        成功 = 1,
        失败 = 2
    }

    public enum OperationTarget
    {
        项目 = 1,
        任务 = 2,
        用户 = 3,
        分组 = 4,
        会议纪要 = 5,
        日报 = 6
    }
}
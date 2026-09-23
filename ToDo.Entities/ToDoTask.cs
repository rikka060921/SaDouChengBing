namespace ToDo.Entities
{
    using System;
    using System.ComponentModel.DataAnnotations;
    using System.ComponentModel.DataAnnotations.Schema;

    [Table("tasks")]
    /// <summary>
    /// 表示系统中的任务实体。
    /// </summary>
    public class ToDoTask
    {
        /// <summary>
        /// 任务主键 ID。
        /// </summary>
        [Key]
        public int Id { get; set; }
      
        /// <summary>
        /// 任务标题，必填，最大长度 255。
        /// </summary>
        [Required]
        [MaxLength(255)]
        [Display(Name = "任务标题")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// 任务描述，允许为空。
        /// </summary>
        [Display(Name = "任务描述")]
        public string? Description { get; set; }

        /// <summary>
        /// 指派人 ID（分配任务的人），可为空。
        /// </summary>
        [Display(Name = "指派人")]
        public int? AssigneeId { get; set; }

        /// <summary>
        /// 指派人用户对象。
        /// </summary>
        [ForeignKey("AssigneeId")]
        public ApplicationUser? Assignee { get; set; }

        /// <summary>
        /// 创建者用户 ID。
        /// </summary>
        [Required]
        public required int CreatorId { get; set; }

        /// <summary>
        /// 创建者用户对象。
        /// </summary>
        [ForeignKey("CreatorId")]
        public ApplicationUser? Creator { get; set; } = null!;

        /// <summary>
        /// 认领人 ID
        /// </summary>
        public int? ClaimerId { get; set; }

        /// <summary>
        /// 认领人对象
        /// </summary>
        [ForeignKey("ClaimerId")]
        public ApplicationUser? Claimer { get; set; }

        /// <summary>父任务，支持任务拆分后的层级关系。</summary>
        public int? ParentTaskId { get; set; }

        [ForeignKey(nameof(ParentTaskId))]
        public ToDoTask? ParentTask { get; set; }

        public ICollection<ToDoTask> SubTasks { get; set; } = new List<ToDoTask>();

        /// <summary>审核人。任务进入待审核后由审核人或项目管理员处理。</summary>
        public int? ReviewerId { get; set; }

        [ForeignKey(nameof(ReviewerId))]
        public ApplicationUser? Reviewer { get; set; }

        public int ReworkCount { get; set; }

        [Required]
        public TaskAssigneeType AssigneeType { get; set; } = TaskAssigneeType.Human;

        [MaxLength(100)]
        public string? AgentName { get; set; }

        /// <summary>数字员工对应的真实 Agent 定义。AgentName 仅保留为历史显示快照。</summary>
        public int? AgentDefinitionId { get; set; }

        [ForeignKey(nameof(AgentDefinitionId))]
        public AgentDefinition? AgentDefinition { get; set; }

        public AgentTaskExecutionStatus AgentExecutionStatus { get; set; } = AgentTaskExecutionStatus.None;
        public int AgentAssignmentVersion { get; set; }
        public DateTime? AgentLastRunAt { get; set; }

        [MaxLength(2000)]
        public string AgentLastError { get; set; } = string.Empty;

        /// <summary>只有未结束且尚未分配的人工任务允许成员认领。</summary>
        [NotMapped]
        public bool CanBeClaimedByHuman =>
            !IsDeleted
            && AssigneeType == TaskAssigneeType.Human
            && !AssigneeId.HasValue
            && Status is TaskStatus.NotStarted or TaskStatus.InProgress;

        /// <summary>
        /// 统一设置任务状态并维护状态、完成标记和进度之间的不变量。
        /// 已完成任务固定为 100%；其他状态最多为 99%，避免出现“进行中 100%”。
        /// </summary>
        public void SetStatus(TaskStatus status, int? progress = null)
        {
            Status = status;
            if (status == TaskStatus.Completed)
            {
                Progress = 100;
                IsCompleted = true;
                return;
            }

            Progress = Math.Clamp(progress ?? Progress, 0, 99);
            IsCompleted = false;
        }

        /// <summary>统一维护人工审核后的任务与 Agent 状态不变量。</summary>
        public void ApplyReviewDecision(bool approved)
        {
            if (approved)
            {
                SetStatus(TaskStatus.Completed);
                if (AssigneeType == TaskAssigneeType.DigitalEmployee)
                    AgentExecutionStatus = AgentTaskExecutionStatus.Completed;
                return;
            }

            SetStatus(TaskStatus.InProgress);
            if (AssigneeType == TaskAssigneeType.DigitalEmployee)
                AgentExecutionStatus = AgentTaskExecutionStatus.Pending;
            ReworkCount++;
        }

        /// <summary>乐观并发版本，每次任务变更自动递增。</summary>
        public int ConcurrencyVersion { get; set; } = 1;

        /*
         * 
         通过“责任人”和“指派人”字段的组合来判断：

             责任人有值，指派人无值 → 责任人主动认领任务。

             责任人有值，指派人有值 → 任务是指派给责任人的。
         */

        /// <summary>
        /// 任务状态，默认值为 NotStarted。
        /// </summary>
        [Required]
        [Display(Name = "任务状态")]
        public TaskStatus Status { get; set; } = TaskStatus.NotStarted;

        /// <summary>
        /// 当前任务进度（0-100）。
        /// </summary>
        [Range(0, 100)]
        public int Progress { get; set; } = 0;

        /// <summary>
        /// 所属任务分组 ID
        /// </summary>
        [Display(Name = "任务分组")]
        public int? GroupId { get; set; }

        /// <summary>
        /// 所属任务分组对象。
        /// </summary>
        [ForeignKey("GroupId")]
        public TaskGroup? Group { get; set; }

        /// <summary>
        /// 任务开始时间。
        /// </summary>
        public DateTime? StartTime { get; set; }

        /// <summary>
        /// 任务结束时间。
        /// </summary>
        [Display(Name = "任务结束时间")]
        public DateTime? EndTime { get; set; }

        /// <summary>
        /// 任务优先级，默认值为 Medium。
        /// </summary>
        [Required]
        [Display(Name = "优先级")]
        public TaskPriority Priority { get; set; } = TaskPriority.Medium;

        /// <summary>
        /// 任务创建时间，默认为当前时间。
        /// </summary>
        public DateTime CreatedAt { get; set; } = AppTime.Now;

        /// <summary>
        /// 任务更新时间，默认为当前时间。
        /// </summary>
        public DateTime UpdatedAt { get; set; } = AppTime.Now;
        /// <summary>
        /// 一对多，任务拥有多个变更记录
        /// </summary>
        public ICollection<ChangeLog> ChangeLogs { get; set; } = new List<ChangeLog>();

        /// <summary>
        /// 任务完成状态标识
        /// <para>true：已完成；false：未完成</para>
        /// </summary>
        public bool IsCompleted { get; set; }

        /// <summary>
        /// 任务删除状态标识（逻辑删除）
        /// <para>true：已删除；false：正常</para>
        /// </summary>
        public bool IsDeleted { get; set; } = false;

        /// <summary>
        /// 关联项目的唯一标识（外键）
        /// <para>与Project实体的Id字段关联，实现"多对一"关系（多个任务属于一个项目）</para>
        /// </summary>
        [Required]
        public int ProjectId { get; set; }

        /// <summary>
        /// 导航属性，用于EF Core关联查询项目信息
        /// <para>通过该属性可直接访问任务所属的Project实体数据（如项目名称、创建人等）</para>
        /// </summary>
        public Project? Project { get; set; }

        public ICollection<TaskComment> Comments { get; set; } = new List<TaskComment>();
        public ICollection<TaskLabelLink> LabelLinks { get; set; } = new List<TaskLabelLink>();
        public ICollection<AgentWorkItem> AgentWorkItems { get; set; } = new List<AgentWorkItem>();
    }
}

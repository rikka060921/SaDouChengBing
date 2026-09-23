namespace ToDo.Entities
{
    using System.ComponentModel.DataAnnotations;

    /// <summary>
    /// 表示任务的当前状态。
    /// </summary>
    public enum TaskStatus
    {
        /// <summary>
        /// 任务尚未开始。
        /// </summary>
        [Display(Name = "未开始")]
        NotStarted = 0,

        /// <summary>
        /// 任务正在进行中。
        /// </summary>
        [Display(Name = "进行中")]
        InProgress = 1,

        /// <summary>
        /// 任务已完成。
        /// </summary>
        [Display(Name = "已完成")]
        Completed = 2,

        /// <summary>
        /// 任务已被取消。
        /// </summary>
        [Display(Name = "已取消")]
        Cancelled = 3,

        /// <summary>
        /// 执行人已完成，等待负责人或项目管理员人工确认。
        /// </summary>
        [Display(Name = "待审核")]
        PendingConfirmation = 4
    }

    /// <summary>
    /// 表示任务的优先级。
    /// </summary>
    public enum TaskPriority
    {
        /// <summary>
        /// 优先级为高。
        /// </summary>
        [Display(Name = "高")]
        High,

        /// <summary>
        /// 优先级为中。
        /// </summary>
        [Display(Name = "中")]
        Medium,

        /// <summary>
        /// 优先级为低。
        /// </summary>
        [Display(Name = "低")]
        Low
    }

    /// <summary>
    /// 任务执行主体。第一阶段保留人工和数字员工两种分配类型，具体 Agent 在后续阶段接入。
    /// </summary>
    public enum TaskAssigneeType
    {
        [Display(Name = "团队成员")]
        Human = 0,

        [Display(Name = "数字员工")]
        DigitalEmployee = 1
    }
}

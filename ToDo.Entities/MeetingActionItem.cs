using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum MeetingActionSupervisionStatus
{
    PendingLink = 0,
    NeedsDefinition = 1,
    OnTrack = 2,
    DueSoon = 3,
    Overdue = 4,
    Blocked = 5,
    Completed = 6,
    Cancelled = 7
}

public enum MeetingActionSupervisionEventType
{
    StateChanged = 0,
    Reminder = 1,
    Escalation = 2,
    Acknowledged = 3
}

public class MeetingActionItem
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int MeetingMinutesId { get; set; }

    /// <summary>
    /// 行动项归属的项目（AI 解析时判定，允许人工修改）。
    /// 历史数据为 null，读取时回退用 MeetingMinutes.ProjectId。
    /// </summary>
    public int? ProjectId { get; set; }

    /// <summary>任务标题（短名称）</summary>
    [Required]
    [MaxLength(255)]
    public string Title { get; set; } = string.Empty;

    /// <summary>任务详细描述</summary>
    [Column(TypeName = "text")]
    public string? Description { get; set; }

    /// <summary>原始完整内容（兼容旧数据）</summary>
    [MaxLength(1000)]
    public string Content { get; set; } = string.Empty;

    /// <summary>优先级：High/Medium/Low</summary>
    [MaxLength(20)]
    public string Priority { get; set; } = "Medium";

    /// <summary>任务分组名称</summary>
    [MaxLength(100)]
    public string? GroupName { get; set; }

    /// <summary>任务初始状态：NotStarted/InProgress/Completed</summary>
    [MaxLength(30)]
    public string TaskStatus { get; set; } = "NotStarted";

    [MaxLength(100)]
    public string? AssigneeText { get; set; }

    public int? AssigneeId { get; set; }

    /// <summary>截止日期 yyyy-MM-dd</summary>
    public DateTime? Deadline { get; set; }

    public int? MatchedTaskId { get; set; }

    /// <summary>
    /// 关联的会前准备草稿任务ID（草稿 JSON 快照里的 ID，不是数据库主键）。
    /// 用于标记这条行动项对应了会前准备里勾选的哪条任务；草稿里没被关联的任务会显示在"未讨论到的任务"区域。
    /// </summary>
    public int? PrepDraftTaskId { get; set; }

    /// <summary>关联的议题ID（可选）</summary>
    public int? MeetingAgendaId { get; set; }

    /// <summary>
    /// 任务来源决策的序号（对应会议决策 JSON 列表，从1开始）。
    /// 任务由某条会议决策落地执行产生时填写；仅用于页面内定位，决策内容以 SourceDecisionContent 快照为准。
    /// </summary>
    public int? SourceDecisionIndex { get; set; }

    /// <summary>任务来源决策的内容快照（解析时从决策列表复制，决策重解析后已确认任务仍可展示来源）</summary>
    [MaxLength(500)]
    public string? SourceDecisionContent { get; set; }

    /// <summary>是否已确认写入任务</summary>
    public bool IsConfirmed { get; set; }

    public string SyncStatus { get; set; } = "待处理";

    [MaxLength(500)]
    public string? SyncMessage { get; set; }

    public DateTime CreatedAt { get; set; } = AppTime.Now;

    /// <summary>会议任务写入正式任务后的督办状态，由服务端根据真实任务状态计算。</summary>
    public MeetingActionSupervisionStatus SupervisionStatus { get; set; } = MeetingActionSupervisionStatus.PendingLink;

    public DateTime? LastSupervisedAt { get; set; }
    public DateTime? LastReminderAt { get; set; }
    public int ReminderCount { get; set; }
    public int EscalationLevel { get; set; }

    public int? SupervisionAcknowledgedByUserId { get; set; }
    public DateTime? SupervisionAcknowledgedAt { get; set; }
    public DateTime? SnoozedUntil { get; set; }
    public DateTime? NextCheckpointAt { get; set; }

    [MaxLength(1000)]
    public string SupervisionResolutionNote { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string SupervisionMessage { get; set; } = string.Empty;

    // ========== 新增：区分新增任务和历史任务变更 ==========
    /// <summary>行动项类型：NewTask=新增任务 / TaskChange=历史任务变更</summary>
    [MaxLength(30)]
    public string ActionType { get; set; } = "NewTask";

    /// <summary>变更前的状态（从任务当前值读取）</summary>
    [MaxLength(30)]
    public string? BeforeStatus { get; set; }

    /// <summary>变更后的状态（会议中讨论的值）</summary>
    [MaxLength(30)]
    public string? AfterStatus { get; set; }

    /// <summary>变更前的负责人（从任务当前值读取）</summary>
    public int? BeforeAssigneeId { get; set; }

    /// <summary>变更后的负责人（会议中讨论的值）</summary>
    public int? AfterAssigneeId { get; set; }

    /// <summary>变更前的截止时间（从任务当前值读取）</summary>
    public DateTime? BeforeDeadline { get; set; }

    /// <summary>变更后的截止时间（会议中讨论的值）</summary>
    public DateTime? AfterDeadline { get; set; }

    /// <summary>变更前的优先级（从任务当前值读取）</summary>
    [MaxLength(20)]
    public string? BeforePriority { get; set; }

    /// <summary>变更后的优先级（会议中讨论的值）</summary>
    [MaxLength(20)]
    public string? AfterPriority { get; set; }

    /// <summary>变更说明</summary>
    [MaxLength(500)]
    public string? ChangeDescription { get; set; }

    // ========== 导航属性 ==========
    [ForeignKey(nameof(MeetingMinutesId))]
    public MeetingMinutes? MeetingMinutes { get; set; }

    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }

    [ForeignKey(nameof(AssigneeId))]
    public ApplicationUser? Assignee { get; set; }

    [ForeignKey(nameof(MatchedTaskId))]
    public ToDoTask? MatchedTask { get; set; }

    [ForeignKey(nameof(MeetingAgendaId))]
    public MeetingAgenda? MeetingAgenda { get; set; }

    public ICollection<MeetingActionSupervisionEvent> SupervisionEvents { get; set; } = new List<MeetingActionSupervisionEvent>();
}

/// <summary>会议行动项的状态变更、提醒和升级记录，EventKey 保证同一检查信号不会重复通知。</summary>
[Table("meeting_action_supervision_events")]
public sealed class MeetingActionSupervisionEvent
{
    public long Id { get; set; }
    public int ActionItemId { get; set; }
    [Required]
    public int MeetingMinutesId { get; set; }

    /// <summary>
    /// 督办事件归属的项目（从行动项的 ProjectId 取值，历史数据可能为空）。
    /// </summary>
    public int? ProjectId { get; set; }

    public int? TaskId { get; set; }
    public MeetingActionSupervisionEventType EventType { get; set; }
    public MeetingActionSupervisionStatus Status { get; set; }
    public int EscalationLevel { get; set; }

    [Required, MaxLength(160)]
    public string EventKey { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string RecipientIdsJson { get; set; } = "[]";

    [MaxLength(2000)]
    public string Message { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = AppTime.Now;

    [ForeignKey(nameof(ActionItemId))]
    public MeetingActionItem? ActionItem { get; set; }
}

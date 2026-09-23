using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum AgentTaskExecutionStatus
{
    None = 0,
    Pending = 1,
    Running = 2,
    WaitingApproval = 3,
    Retrying = 4,
    AwaitingConfirmation = 5,
    Completed = 6,
    Failed = 7,
    Paused = 8,
    AwaitingDispatchConfirmation = 9,
    AwaitingPlanConfirmation = 10
}

public static class AgentTaskExecutionStatusExtensions
{
    public static string GetDisplayName(this AgentTaskExecutionStatus status) => status switch
    {
        AgentTaskExecutionStatus.AwaitingPlanConfirmation => "等待确认执行计划",
        AgentTaskExecutionStatus.Pending => "Agent待执行",
        AgentTaskExecutionStatus.Running => "Agent执行中",
        AgentTaskExecutionStatus.WaitingApproval => "等待敏感操作审批",
        AgentTaskExecutionStatus.Retrying => "等待自动重试",
        AgentTaskExecutionStatus.AwaitingConfirmation => "待人工确认",
        AgentTaskExecutionStatus.Completed => "已确认完成",
        AgentTaskExecutionStatus.Failed => "执行失败",
        AgentTaskExecutionStatus.Paused => "已暂停",
        AgentTaskExecutionStatus.AwaitingDispatchConfirmation => "等待确认 Agent 派单",
        _ => "未触发"
    };
}

public enum AgentWorkItemStatus
{
    Pending = 0,
    Running = 1,
    WaitingApproval = 2,
    Retrying = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6,
    Paused = 7,
    WaitingPlanConfirmation = 8
}

public enum AgentWorkTriggerType
{
    TaskAssigned = 0,
    TaskCommentAdded = 1,
    TaskReviewRejected = 2,
    ApprovalResolved = 3,
    ManualRetry = 4
}

[Table("agent_work_items")]
public class AgentWorkItem
{
    public int Id { get; set; }
    public int AgentDefinitionId { get; set; }
    public int ProjectId { get; set; }
    public int TaskId { get; set; }
    public int RequestedByUserId { get; set; }
    public int? AiSessionId { get; set; }
    public int? WaitingApprovalRequestId { get; set; }
    public AgentWorkTriggerType TriggerType { get; set; }
    public int? TriggerEntityId { get; set; }
    public AgentWorkItemStatus Status { get; set; } = AgentWorkItemStatus.Pending;

    [Required, MaxLength(200)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required, MaxLength(32)]
    public string CauseChainId { get; set; } = Guid.NewGuid().ToString("N");

    [Column(TypeName = "text")]
    public string Prompt { get; set; } = string.Empty;

    [Column(TypeName = "text")]
    public string ResultSummary { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string ErrorMessage { get; set; } = string.Empty;

    // 存量工作项保留原标志；新工作项由统一用途策略判断是否需要规划。
    public bool RequiresPlan { get; set; }
    [Column(TypeName = "text")]
    public string ExecutionPlan { get; set; } = string.Empty;
    [MaxLength(2000)]
    public string PlanFeedback { get; set; } = string.Empty;
    [MaxLength(64)]
    public string PlanContextHash { get; set; } = string.Empty;
    public int PlanRevision { get; set; }
    public int PlanAgentVersion { get; set; }
    public int? PlanningSessionId { get; set; }
    public int? PlanApprovedByUserId { get; set; }
    public DateTime? PlanApprovedAt { get; set; }

    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public int StepCount { get; set; }
    public int MaxSteps { get; set; } = 5;
    public DateTime NextRunAt { get; set; } = AppTime.Now;
    public DateTime CreatedAt { get; set; } = AppTime.Now;
    public DateTime UpdatedAt { get; set; } = AppTime.Now;
    public DateTime? LockedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    [ForeignKey(nameof(AgentDefinitionId))]
    public AgentDefinition? AgentDefinition { get; set; }

    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }

    [ForeignKey(nameof(TaskId))]
    public ToDoTask? Task { get; set; }

    [ForeignKey(nameof(RequestedByUserId))]
    public ApplicationUser? RequestedByUser { get; set; }

    [ForeignKey(nameof(AiSessionId))]
    public AiSession? AiSession { get; set; }

    [ForeignKey(nameof(WaitingApprovalRequestId))]
    public ApprovalRequest? WaitingApprovalRequest { get; set; }

    public AgentDeliveryReceipt? DeliveryReceipt { get; set; }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum AgentEventExecutionStatus
{
    Pending = 0,
    Running = 1,
    WaitingApproval = 2,
    Retrying = 3,
    Completed = 4,
    Failed = 5,
    Skipped = 6,
    Cancelled = 7
}

public enum AgentEventConditionOperator
{
    None = 0,
    Equals = 1,
    NotEquals = 2,
    Contains = 3,
    StartsWith = 4,
    GreaterThan = 5,
    LessThan = 6,
    Exists = 7
}

[Table("agent_event_subscriptions")]
public class AgentEventSubscription
{
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string EventType { get; set; } = string.Empty;

    public int AgentDefinitionId { get; set; }
    public int? ProjectId { get; set; }
    public int CreatedByUserId { get; set; }

    [Column(TypeName = "text")]
    public string PromptTemplate { get; set; } = string.Empty;

    public int CooldownSeconds { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public int MaxSteps { get; set; } = 5;
    public int DailyExecutionLimit { get; set; } = 50;
    public int MaxTokenBudget { get; set; } = 20000;

    [MaxLength(160)]
    public string ConditionField { get; set; } = string.Empty;

    public AgentEventConditionOperator ConditionOperator { get; set; }

    [MaxLength(500)]
    public string ConditionValue { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = AppTime.Now;
    public DateTime UpdatedAt { get; set; } = AppTime.Now;

    [ForeignKey(nameof(AgentDefinitionId))]
    public AgentDefinition? AgentDefinition { get; set; }

    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }

    [ForeignKey(nameof(CreatedByUserId))]
    public ApplicationUser? CreatedByUser { get; set; }

    public ICollection<AgentEventExecution> Executions { get; set; } = new List<AgentEventExecution>();
}

[Table("agent_event_executions")]
public class AgentEventExecution
{
    public long Id { get; set; }
    public int SubscriptionId { get; set; }
    public long EventBusMessageId { get; set; }
    public int AgentDefinitionId { get; set; }
    public int? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public int RequestedByUserId { get; set; }
    public int? AiSessionId { get; set; }
    public int? WaitingApprovalRequestId { get; set; }
    public AgentEventExecutionStatus Status { get; set; } = AgentEventExecutionStatus.Pending;

    [Column(TypeName = "text")]
    public string Prompt { get; set; } = string.Empty;

    [Column(TypeName = "text")]
    public string ResultSummary { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string ErrorMessage { get; set; } = string.Empty;

    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public int StepCount { get; set; }
    public int MaxSteps { get; set; } = 5;
    public int MaxTokenBudget { get; set; } = 20000;
    public int ConsumedTokens { get; set; }
    public DateTime NextRunAt { get; set; } = AppTime.Now;
    public DateTime CreatedAt { get; set; } = AppTime.Now;
    public DateTime UpdatedAt { get; set; } = AppTime.Now;
    public DateTime? LockedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    [ForeignKey(nameof(SubscriptionId))]
    public AgentEventSubscription? Subscription { get; set; }

    [ForeignKey(nameof(EventBusMessageId))]
    public EventBusMessage? EventBusMessage { get; set; }

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
}

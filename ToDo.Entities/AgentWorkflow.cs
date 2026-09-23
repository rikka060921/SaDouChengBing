using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum ProjectDocumentCategory
{
    RequirementsAndGoals = 0,
    MeetingMinutes = 1,
    TasksAndPlans = 2,
    DailyReportsAndReviews = 3,
    TechnicalMaterials = 4,
    PoliciesAndStandards = 5,
    Other = 6
}

public static class ProjectDocumentCategoryExtensions
{
    public static string GetDisplayName(this ProjectDocumentCategory category) => category switch
    {
        ProjectDocumentCategory.RequirementsAndGoals => "需求与目标",
        ProjectDocumentCategory.MeetingMinutes => "会议纪要",
        ProjectDocumentCategory.TasksAndPlans => "任务与计划",
        ProjectDocumentCategory.DailyReportsAndReviews => "日报与复盘",
        ProjectDocumentCategory.TechnicalMaterials => "技术资料",
        ProjectDocumentCategory.PoliciesAndStandards => "制度与规范",
        _ => "其他"
    };
}

[Table("agent_document_permissions")]
public class AgentDocumentPermission
{
    public int Id { get; set; }
    public int ProjectId { get; set; }

    [Required, MaxLength(80)]
    public string AgentKey { get; set; } = string.Empty;

    public ProjectDocumentCategory Category { get; set; } = ProjectDocumentCategory.Other;
    public int? CategoryId { get; set; }
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public int UpdatedById { get; set; }
    public DateTime UpdatedAt { get; set; } = AppTime.Now;

    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }

    [ForeignKey(nameof(CategoryId))]
    public DocumentCategory? CustomCategory { get; set; }
}

public enum AgentDocumentAccessType
{
    Read = 0,
    Write = 1
}

[Table("agent_document_access_logs")]
public class AgentDocumentAccessLog
{
    public long Id { get; set; }
    public int ProjectId { get; set; }
    public int? DocumentId { get; set; }
    public int? AiSessionId { get; set; }
    public int? UserId { get; set; }

    [Required, MaxLength(80)]
    public string AgentKey { get; set; } = string.Empty;

    public ProjectDocumentCategory Category { get; set; } = ProjectDocumentCategory.Other;
    public int? CategoryId { get; set; }
    public AgentDocumentAccessType AccessType { get; set; }
    public bool IsAllowed { get; set; }

    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = AppTime.Now;
}

public enum AiSessionMessageRole
{
    System = 0,
    User = 1,
    Assistant = 2,
    Tool = 3
}

[Table("ai_session_messages")]
public class AiSessionMessage
{
    public long Id { get; set; }
    public int AiSessionId { get; set; }
    public AiSessionMessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;

    [MaxLength(100)]
    public string ToolName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = AppTime.Now;

    [ForeignKey(nameof(AiSessionId))]
    public AiSession? Session { get; set; }
}

public enum AgentToolCallStatus
{
    Proposed = 0,
    PendingApproval = 1,
    Executed = 2,
    Rejected = 3,
    Failed = 4
}

[Table("agent_tool_calls")]
public class AgentToolCall
{
    public int Id { get; set; }
    public int AiSessionId { get; set; }

    [Required, MaxLength(100)]
    public string ToolName { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string IdempotencyKey { get; set; } = string.Empty;

    public string ArgumentsJson { get; set; } = "{}";
    public string ResultJson { get; set; } = "{}";
    public bool RequiresApproval { get; set; }
    public AgentRiskLevel RiskLevel { get; set; } = AgentRiskLevel.High;
    public AgentToolReviewMode ReviewMode { get; set; } = AgentToolReviewMode.HumanApproval;

    [MaxLength(1000)]
    public string ReviewReason { get; set; } = string.Empty;

    public AgentToolCallStatus Status { get; set; } = AgentToolCallStatus.Proposed;
    public int? ApprovalRequestId { get; set; }
    public DateTime CreatedAt { get; set; } = AppTime.Now;
    public DateTime? CompletedAt { get; set; }

    [ForeignKey(nameof(AiSessionId))]
    public AiSession? Session { get; set; }

    [ForeignKey(nameof(ApprovalRequestId))]
    public ApprovalRequest? ApprovalRequest { get; set; }
}

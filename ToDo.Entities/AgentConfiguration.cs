using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum AgentContextSource
{
    Project = 0,
    ProjectTasks = 1,
    SelectedTask = 2,
    TaskComments = 3,
    Meetings = 4,
    Documents = 5,
    Reports = 6
}

public enum AgentRiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2
}

public enum AgentToolReviewMode
{
    Direct = 0,
    AiReview = 1,
    HumanApproval = 2
}

[Table("agent_tool_permissions")]
public class AgentToolPermission
{
    public int Id { get; set; }
    public int AgentDefinitionId { get; set; }

    [Required, MaxLength(100)]
    public string ToolName { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;
    public bool RequiresApproval { get; set; } = true;
    public AgentToolReviewMode ReviewMode { get; set; } = AgentToolReviewMode.HumanApproval;
    public DateTime UpdatedAt { get; set; } = AppTime.Now;

    [ForeignKey(nameof(AgentDefinitionId))]
    public AgentDefinition? AgentDefinition { get; set; }
}

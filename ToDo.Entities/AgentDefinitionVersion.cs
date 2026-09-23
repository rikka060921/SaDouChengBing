using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

/// <summary>Agent 每次保存后的不可变配置快照，用于运行追溯和审计。</summary>
[Table("agent_definition_versions")]
public sealed class AgentDefinitionVersion
{
    public long Id { get; set; }
    public int AgentDefinitionId { get; set; }
    public int Version { get; set; }
    public int? ChangedByUserId { get; set; }

    [Required, Column(TypeName = "longtext")]
    public string SnapshotJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = AppTime.Now;

    [ForeignKey(nameof(AgentDefinitionId))]
    public AgentDefinition? AgentDefinition { get; set; }

    [ForeignKey(nameof(ChangedByUserId))]
    public ApplicationUser? ChangedByUser { get; set; }
}

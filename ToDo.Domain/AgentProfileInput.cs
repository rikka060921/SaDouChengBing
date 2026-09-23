using System.ComponentModel.DataAnnotations;

namespace ToDo.Domain;

/// <summary>日常编辑白名单，不接受模型、能力、工具、授权或运行状态。</summary>
public sealed class AgentProfileInput
{
    [Required, StringLength(120)] public string Name { get; set; } = string.Empty;
    [Required, StringLength(1000)] public string Description { get; set; } = string.Empty;
    [Required, StringLength(20000)] public string Instructions { get; set; } = string.Empty;
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public class TaskComment
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int TaskId { get; set; }

    [Required]
    public int AuthorId { get; set; }

    [Required]
    [MaxLength(2000)]
    public string Content { get; set; } = string.Empty;

    public bool IsAiGenerated { get; set; }

    [MaxLength(80)]
    public string? AgentKey { get; set; }

    public int? AiSessionId { get; set; }
    public DateTime CreatedAt { get; set; } = AppTime.Now;

    [ForeignKey(nameof(TaskId))]
    public ToDoTask? Task { get; set; }

    [ForeignKey(nameof(AuthorId))]
    public ApplicationUser? Author { get; set; }
}

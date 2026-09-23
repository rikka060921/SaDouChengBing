using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public class TaskLabel
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int ProjectId { get; set; }

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(20)]
    public string Color { get; set; } = "#6c757d";

    public bool IsDeleted { get; set; }

    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }

    public ICollection<TaskLabelLink> TaskLinks { get; set; } = new List<TaskLabelLink>();
}

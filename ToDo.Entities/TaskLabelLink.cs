using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public class TaskLabelLink
{
    public int TaskId { get; set; }
    public int LabelId { get; set; }

    [ForeignKey(nameof(TaskId))]
    public ToDoTask? Task { get; set; }

    [ForeignKey(nameof(LabelId))]
    public TaskLabel? Label { get; set; }
}

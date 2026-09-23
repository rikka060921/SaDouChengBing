using System.ComponentModel.DataAnnotations;

namespace ToDo.Entities;

/// <summary>与任务在同一次事务中写入，防止拆分结果重试或并发提交时重复创建。</summary>
public sealed class TaskSplitSubmission
{
    [Key, MaxLength(80)]
    public string Id { get; set; } = string.Empty;
    public int CreatedTaskCount { get; set; }
    public DateTime CreatedAt { get; set; } = AppTime.Now;
}

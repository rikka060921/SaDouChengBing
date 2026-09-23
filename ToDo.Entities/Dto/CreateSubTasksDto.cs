using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ToDo.Entities.Dto
{
    /// <summary>
    /// 批量创建子任务数据传输对象
    /// </summary>
    public class CreateSubTasksDto
    {
        /// <summary>
        /// 父任务ID
        /// </summary>
        [Required]
        public int ParentTaskId { get; set; }

        /// <summary>
        /// 子任务列表
        /// </summary>
        [Required]
        [MinLength(1, ErrorMessage = "至少需要一个子任务")]
        public List<string> SubTaskTitles { get; set; } = new();

        /// <summary>
        /// 子任务分组ID
        /// </summary>
        public int? GroupId { get; set; }

        /// <summary>
        /// 子任务优先级（默认中等）
        /// </summary>
        public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    }
}
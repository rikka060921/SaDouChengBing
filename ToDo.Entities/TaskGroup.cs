using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ToDo.Entities
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel.DataAnnotations;
    using System.ComponentModel.DataAnnotations.Schema;

    /// <summary>
    /// 表示任务的分组，用于组织和管理一组相关任务。
    /// </summary>
    public class TaskGroup
    {
        /// <summary>
        /// 任务分组的主键 ID。
        /// </summary>
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// 分组名称，必填，最大长度 255。
        /// </summary>
        [Required]
        [MaxLength(255)]
        [Display(Name = "分组名称")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 分组描述，允许为空。
        /// </summary>
        [Display(Name = "分组描述")]
        public string? Description { get; set; }

        /// <summary>
        /// 创建者用户的 ID。
        /// </summary>
        public required int CreatorId { get; set; }

        /// <summary>
        /// 创建者用户对象。
        /// </summary>
        [ForeignKey("CreatorId")]
        public ApplicationUser? Creator { get; set; } = null!;

        /// <summary>
        /// 创建时间，默认值为当前时间。
        /// </summary>
        public DateTime CreatedAt { get; set; } = AppTime.Now;

        /// <summary>
        /// 最后更新时间，默认值为当前时间。
        /// </summary>
        public DateTime UpdatedAt { get; set; } = AppTime.Now;

        /// <summary>
        /// 导航属性：该分组下的所有任务集合。
        /// </summary>
        public List<ToDoTask> Tasks { get; set; } = new List<ToDoTask>();

        /// <summary>
        /// 关联项目的唯一标识（外键）
        /// <para>与Project实体的Id字段关联，实现"多对一"关系（多个任务属于一个项目）</para>
        /// </summary>
        [Required]
        public int ProjectId { get; set; }

        /// <summary>
        /// 逻辑删除
        /// </summary>
        public bool IsDeleted { get; set; } = false;

        /// <summary>
        /// 导航属性，用于EF Core关联查询项目信息
        /// <para>通过该属性可直接访问任务所属的Project实体数据（如项目名称、创建人等）</para>
        /// </summary>
        public Project? Project { get; set; }
    }
}

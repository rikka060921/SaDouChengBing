using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ToDo.Entities;

namespace ToDo.Entities
{
    public class ProjectUser
    {
        [Key, Column(Order = 0)]
        public int ProjectId { get; set; }

        [Key, Column(Order = 1)]
        public int UserId { get; set; }

        /// <summary>
        /// 是否为项目创建者
        /// </summary>
        public bool IsCreator { get; set; } = false;

        /// <summary>
        /// 是否为项目管理员（可替代 ProjectRole 枚举，根据需求二选一）
        /// </summary>
        public bool IsProjectAdmin { get; set; } = false;

        /// <summary>
        /// 项目内角色（0：管理员；1：成员）
        /// </summary>
        public int ProjectRole { get; set; } = 1; // 默认成员

        /// <summary>
        /// 导航属性：关联项目
        /// </summary>
        public Project Project { get; set; } = null!;

        /// <summary>
        /// 导航属性：关联用户
        /// </summary>
        public ApplicationUser User { get; set; } = null!;

      
    }
}
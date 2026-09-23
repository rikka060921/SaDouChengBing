using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities
{
    /// <summary>
    /// 用户性别枚举（移除Other选项）
    /// </summary>
    public enum Gender
    {
        Male,   // 男
        Female  // 女
    }

    /// <summary>
    /// 用户角色枚举（取消访客角色，仅保留系统管理员和团队成员）
    /// </summary>
    public enum UserRole
    {
  
        systemAdmin,    // 系统管理员
        teamMember
    }

    /// <summary>
    /// 用户账号状态枚举
    /// </summary>
    public enum UserStatus
    {
        Active,    // 活跃
        Inactive,  // 未激活
        Locked     // 锁定
    }

    /// <summary>
    /// 应用程序用户实体（适配新角色体系和项目关系）
    /// </summary>
    public class ApplicationUser : IdentityUser<int>
    {
        /// <summary>
        /// 用户真实姓名
        /// </summary>
        [MaxLength(50)]
        public string? RealName { get; set; } = string.Empty;

        /// <summary>
        /// 用户性别（仅保留男/女）
        /// </summary>
        public Gender Gender { get; set; } = Gender.Male;

        /// <summary>
        /// 用户角色（默认团队成员，取消regularUser）
        /// </summary>
        public UserRole Role { get; set; } = UserRole.teamMember;

        /// <summary>
        /// 用户账号状态
        /// </summary>
        public UserStatus Status { get; set; } = UserStatus.Active;

        /// <summary>
        /// 用户创建时间（默认为当前时间）
        /// </summary>
        public DateTime CreatedAt { get; set; } = AppTime.Now;

        /// <summary>
        /// 用户更新时间（未更新时为 null）
        /// </summary>
        public DateTime? UpdatedAt { get; set; } = null;

        /// <summary>
        /// 用户是否已删除
        /// </summary>
        public bool IsDeleted { get; set; } = false;
        
        /// <summary>
        /// 用户删除时间
        /// </summary>
        public DateTime? DeletedAt { get; set; }

        /// <summary>
        /// 用户身份证号（必须唯一）
        /// </summary>
        [MaxLength(30)]
        public string? IdentityNumber { get; set; } = string.Empty;

        /// <summary>
        /// 用户头像 URL
        /// </summary>
        [MaxLength(200)]
        public string? AvatarUrl { get; set; } = string.Empty;

        /// <summary>
        /// 角色显示文本（非数据库字段，用于前端展示）
        /// </summary>
        [NotMapped]
        public string RolesDisplay { get; set; } = string.Empty;

        /// <summary>
        /// 用户手机号
        /// </summary>
        [MaxLength(20)]
        public new string? PhoneNumber { get; set; } = string.Empty;

        /// <summary>
        /// 是否为项目管理员（通过项目成员关系判断，替代原创建项目数量判断）
        /// </summary>
        [NotMapped]
        public bool IsProjectManager => ProjectUsers.Any(pu => pu.ProjectRole == 0); // 0=项目管理员

        // 导航属性：用户创建的项目（一对多）
        public ICollection<Project> CreatedProjects { get; set; } = new List<Project>();

        // 导航属性：用户负责的项目（一对多，对应Project.LeaderUser）
        public ICollection<Project> LeadedProjects { get; set; } = new List<Project>();

        // 导航属性：用户参与的项目（多对多，通过ProjectUser关联）
        public ICollection<ProjectUser> ProjectUsers { get; set; } = new List<ProjectUser>();

        // 导航属性：用户提交的日报（一对多）
        public ICollection<DailyReport> ReportedDailyReports { get; set; } = new List<DailyReport>();

        // 导航属性：用户被分配的任务（一对多）
        public ICollection<ToDoTask> AssignedTasks { get; set; } = new List<ToDoTask>();

        // 导航属性：用户认领的任务（一对多）
        public ICollection<ToDoTask> ClaimedTasks { get; set; } = new List<ToDoTask>();

        // 导航属性：用户创建的任务（一对多）
        public ICollection<ToDoTask> CreatedTasks { get; set; } = new List<ToDoTask>();

        // 导航属性：用户创建的任务分组（一对多）
        public ICollection<TaskGroup> CreatedTaskGroups { get; set; } = new List<TaskGroup>();
    }
}

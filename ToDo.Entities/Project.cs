using ToDo.Entities;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;


public enum ProjectRole
{
    Admin = 0,    // 项目管理员
    Member = 1    // 项目成员
}

public enum ProjectStatus
{
    Active = 0,    // 活跃状态
    Archived = 1   // 已归档
}

public class Project
{
    /// <summary>
    /// 项目ID（主键）
    /// </summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>a
    /// 项目名称（必填，最大长度255）
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 项目描述（可选，最大长度1000）
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; set; } = string.Empty;

    /// <summary>
    /// 项目创建时间（默认当前时间）
    /// </summary>
    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 创建人ID（外键，关联 ApplicationUser.Id，必填）
    /// </summary>
    [Required]
    [MaxLength(450)] // 与AspNetUsers表的Id字段长度匹配
    [ForeignKey(nameof(CreatedByUser))]
    public int CreatedByUserId { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    // 新增方法判断是否可修改
    public bool CanModify => Status == ProjectStatus.Active;

    /// <summary>
    /// 用户更新时间（未更新时为 null）
    /// </summary>
    public DateTime? UpdatedAt { get; set; } = null;

    /// <summary>
    /// 项目是否已删除（逻辑删除）
    /// </summary>
    public bool IsDeleted { get; set; } = false;

    [Column(TypeName = "text")] // 适用于长文本

    // 新增加密状态字段（0：公开；1：负责人及成员可见；2：仅负责人可见）
    public char IsEncrypted { get; set; } = '0';

    // 明确项目负责人（与创建者可不同，支持更换）
    public int LeaderUserId { get; set; }
    public ApplicationUser LeaderUser { get; set; } = null!;
    public string Requirements { get; set; } = string.Empty; // 添加默认值

    /// <summary>
    /// 导航属性：项目创建人（一对多）
    /// </summary>
    [Required]
    public ApplicationUser CreatedByUser { get; set; } = null!;

    /// <summary>
    /// 导航属性：项目成员（多对多，通过 ProjectUser 中间表）
    /// </summary>
    public ICollection<ProjectUser> ProjectUsers { get; set; } = new List<ProjectUser>();

    /// <summary>
    /// 导航属性：项目下的任务（一对多）
    /// </summary>
    public ICollection<ToDoTask> Tasks { get; set; } = new List<ToDoTask>();

    /// <summary>
    /// 导航属性：项目下的任务分组（一对多）
    /// </summary>
    public ICollection<TaskGroup> TaskGroups { get; set; } = new List<TaskGroup>();

    /// <summary>
    /// 导航属性：项目会议纪要（一对多）
    /// </summary>
    public ICollection<MeetingMinutes> MeetingMinutes { get; set; } = new List<MeetingMinutes>();

    /// <summary>
    /// 导航属性：项目日报（一对多）
    /// </summary>
    public ICollection<DailyReport> DailyReports { get; set; } = new List<DailyReport>();

    /// <summary>
    /// 导航属性：项目变更日志（一对多）
    /// </summary>
    public ICollection<ChangeLog> ChangeLogs { get; set; } = new List<ChangeLog>();

    /// <summary>
    /// 导航属性：项目成员（直接多对多，与 ProjectUsers 关联同一关系）
    /// </summary>
    public ICollection<ApplicationUser> Users { get; set; } = new List<ApplicationUser>();
    
}

/// <summary>
/// 项目列表展示用DTO，封装项目列表页所需核心数据及权限判断依据
/// 适配角色：系统管理员、团队成员（取消访客角色）
/// </summary>
namespace ToDo.Entities.Dto
{
    public class ProjectListDto
    {
        // 项目基础信息
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string CreatedByUserName { get; set; } = string.Empty; // 创建人用户名
        public int CreatedByUserId { get; set; } // 创建人ID
        public string? Requirements { get; set; } // 项目需求（可选展示）

        // 项目状态信息
        public ProjectStatus Status { get; set; }
        public bool CanModify { get; set; }
        public char IsEncrypted { get; set; } = '0'; // 加密状态（0：公开；1：负责人及成员可见；2：仅负责人可见）

        // 项目负责人信息（支持更换负责人功能）
        public int LeaderUserId { get; set; } // 当前负责人ID
        public string LeaderUserName { get; set; } = string.Empty; // 当前负责人用户名

        // 权限判断关键属性（用于前端控制按钮显示）
        public bool IsCurrentUserSystemAdmin { get; set; } // 是否为系统管理员（最高权限）
        public bool IsCurrentUserLeader { get; set; } // 是否为当前项目负责人（可执行归档、更换负责人等操作）
        public bool IsCurrentUserProjectAdmin { get; set; } // 是否为项目管理员（创建者或被分配的管理员）
        public bool IsCurrentUserMember { get; set; } // 是否为项目成员（仅参与项目）
        public bool IsProjectMember { get; set; }
        // 新增成员列表
        public List<ProjectUserDto> ProjectUsers { get; set; } = new();
        // 新增：标记当前用户是否为项目管理员
        public bool IsCurrentUserAdmin { get; set; }
    }
    // 新增 ProjectUserDto
    public class ProjectUserDto
    {
        public int UserId { get; set; }
        public int ProjectRole { get; set; } // 0=Admin, 1=Member
    }
}
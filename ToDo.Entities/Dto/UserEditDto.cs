// ToDo.Entities/Dto/UserEditDto.cs
using System.ComponentModel.DataAnnotations;
using ToDo.Entities;

namespace ToDo.Entities.Dto
{
    public class UserEditDto
    {
        public int Id { get; set; }

        [Required]
        public string UserName { get; set; } = string.Empty;

        [Required(ErrorMessage = "真实姓名不能为空")]
        [RegularExpression(@"^[\u4e00-\u9fa5]+$", ErrorMessage = "真实姓名只能包含汉字且不能有空格")]
        public string RealName { get; set; } = string.Empty;

        [Required(ErrorMessage = "邮箱不能为空")]
        [EmailAddress(ErrorMessage = "邮箱格式不正确")]
        [RegularExpression(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", ErrorMessage = "邮箱中不能包含空格")]
        public string Email { get; set; } = string.Empty;

        public Gender Gender { get; set; }

        [Phone(ErrorMessage = "电话号码格式不正确")]
        [RegularExpression(@"^1[3-9]\d{9}$", ErrorMessage = "请输入有效的中国手机号码")]
        [Required(ErrorMessage = "电话号码不能为空")]
        public string? PhoneNumber { get; set; }

        public string? IdentityNumber { get; set; }

        public string? AvatarUrl { get; set; }

        // 添加 Role 属性
        public UserRole Role { get; set; }
        public UserStatus Status { get; set; }
    }
}
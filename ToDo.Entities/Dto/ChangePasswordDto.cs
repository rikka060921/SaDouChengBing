// ToDo.Entities/Dto/ChangePasswordDto.cs
using System.ComponentModel.DataAnnotations;

namespace ToDo.Entities.Dto
{
    public class ChangePasswordDto
    {
        [Required(ErrorMessage = "当前密码是必填项")]
        [DataType(DataType.Password)]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "新密码是必填项")]
        [StringLength(100, ErrorMessage = "密码长度至少为 {2} 个字符", MinimumLength = 6)]
        [DataType(DataType.Password)]
        public string NewPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "确认新密码是必填项")]
        [DataType(DataType.Password)]
        [Compare("NewPassword", ErrorMessage = "密码和确认密码不匹配")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}

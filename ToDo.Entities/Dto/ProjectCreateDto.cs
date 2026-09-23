using System.ComponentModel.DataAnnotations;

namespace ToDo.Entities.Dto
{
    /// <summary>
    /// 项目创建数据传输对象
    /// </summary>
    public class ProjectCreateDto
    {
        [Required(ErrorMessage = "项目名称不能为空")]
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Requirements { get; set; } = string.Empty;
    }
}

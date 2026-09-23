using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ToDo.Entities
{
    public class MeetingAttachment
    {
        public int Id { get; set; }

        // 所属会议纪要ID（外键）
        public int MeetingMinutesId { get; set; }
        public int UploadedByUserId { get; set; } // 上传人ID

        // 导航属性：关联到会议纪要（必选）
        [Required]
        public MeetingMinutes MeetingMinutes { get; set; } = null!;

        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty; // 文件存储路径
        public string ContentType { get; set; } = string.Empty; // MIME类型
        public long FileSize { get; set; } // 文件大小
        public DateTime UploadedAt { get; set; } = AppTime.Now;

        // 新增：逻辑删除标记
        public bool IsDeleted { get; set; } = false;

        // 新增：最后修改时间（用于跟踪更新）
        public DateTime LastModifiedAt { get; set; } = AppTime.Now;

        // 移除与用户相关的属性（不再关联上传人）
        // 删除：public int UploadedByUserId { get; set; }
        // 删除：public ApplicationUser UploadedByUser { get; set; }
    }
}

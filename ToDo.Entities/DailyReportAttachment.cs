using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ToDo.Entities
{
    /// <summary>
    /// 项目日报附件实体类
    /// </summary>
    public class DailyReportAttachment
    {
        /// <summary>
        /// 附件ID（主键）
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 所属项目日报ID（外键，关联DailyReport表的主键）
        /// </summary>
        public int DailyReportId { get; set; }

        public int UploadedByUserId { get; set; } // 上传人ID


        /// <summary>
        /// 附件原始文件名
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// 附件存储路径（相对路径）
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// 文件MIME类型（如application/pdf、image/jpeg等）
        /// </summary>
        public string ContentType { get; set; } = string.Empty;

        /// <summary>
        /// 文件大小（字节）
        /// </summary>
        public long FileSize { get; set; }

        /// <summary>
        /// 上传时间
        /// </summary>
        public DateTime UploadedAt { get; set; } = AppTime.Now;

        /// <summary>
        /// 导航属性：关联的项目日报
        /// </summary>
        public DailyReport DailyReport { get; set; } = null!;

        // 新增：逻辑删除标记（用于跟踪删除状态，不实际从数据库删除）
        public bool IsDeleted { get; set; } = false;

        // 新增：最后修改时间（用于跟踪附件的更新操作）
        public DateTime LastModifiedAt { get; set; } = AppTime.Now;
    }
}

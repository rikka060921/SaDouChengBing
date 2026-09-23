using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities
{
    public enum AgendaSourceType
    {
        Manual = 0,
        PreviousMeetingAction = 1,
        OpenTask = 2
    }

    public enum AgendaStatus
    {
        Active = 0,
        Archived = 1
    }

    public class MeetingAgenda
    {
        public int Id { get; set; }

        [Required]
        public int ProjectId { get; set; }

        [Required(ErrorMessage = "议题标题不能为空")]
        [StringLength(500, ErrorMessage = "议题标题不能超过500个字符")]
        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        public AgendaSourceType SourceType { get; set; }

        public int? SourceId { get; set; }

        public int SortOrder { get; set; }

        public bool IsDeleted { get; set; } = false;

        public AgendaStatus Status { get; set; } = AgendaStatus.Active;

        public DateTime? ArchivedAt { get; set; }

        public int? ArchivedByMeetingId { get; set; }

        public DateTime CreatedAt { get; set; } = AppTime.Now;

        public DateTime? LastModifiedAt { get; set; }

        [ForeignKey("ProjectId")]
        public virtual Project Project { get; set; } = null!;

        public ICollection<MeetingAgendaRelation> MeetingRelations { get; set; } = new List<MeetingAgendaRelation>();
    }
}

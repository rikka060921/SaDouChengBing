using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public class MeetingAgendaRelation
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int MeetingMinutesId { get; set; }

    [Required]
    public int MeetingAgendaId { get; set; }

    public DateTime CreatedAt { get; set; } = AppTime.Now;

    [ForeignKey(nameof(MeetingMinutesId))]
    public virtual MeetingMinutes MeetingMinutes { get; set; } = null!;

    [ForeignKey(nameof(MeetingAgendaId))]
    public virtual MeetingAgenda MeetingAgenda { get; set; } = null!;
}

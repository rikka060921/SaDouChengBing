using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public class MeetingVersion
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int MeetingMinutesId { get; set; }

    [Required]
    public int VersionNumber { get; set; }

    [Required]
    [MaxLength(200)]
    public string MeetingTitle { get; set; } = string.Empty;

    [Required]
    public string MeetingContent { get; set; } = string.Empty;

    public bool IsDraft { get; set; }
    public int EditorId { get; set; }
    public DateTime CreatedAt { get; set; } = AppTime.Now;

    [ForeignKey(nameof(MeetingMinutesId))]
    public MeetingMinutes? MeetingMinutes { get; set; }

    [ForeignKey(nameof(EditorId))]
    public ApplicationUser? Editor { get; set; }
}

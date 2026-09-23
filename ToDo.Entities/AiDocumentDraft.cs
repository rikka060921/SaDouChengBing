using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum DraftAuditStatus
{
    Pending = 0,
    Pass = 1,
    Reject = 2
}

[Table("ai_document_drafts")]
public class AiDocumentDraft
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int SourceDocumentId { get; set; }

    [Required, MaxLength(255)]
    public string DraftFileName { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string DraftFilePath { get; set; } = string.Empty;

    public string? OriginalContent { get; set; }
    public string? RevisedContent { get; set; }

    [MaxLength(500)]
    public string? ChangeDescription { get; set; }

    [Required, MaxLength(80)]
    public string AgentKey { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? AgentDisplayName { get; set; }

    public DraftAuditStatus AuditStatus { get; set; } = DraftAuditStatus.Pending;
    public string? AuditUserId { get; set; }
    public DateTime? AuditTime { get; set; }
    public DateTime ExpireTime { get; set; } = AppTime.Now.AddDays(30);
    public DateTime CreatedAt { get; set; } = AppTime.Now;

    [ForeignKey(nameof(ProjectId))]
    public Project? Project { get; set; }

    [ForeignKey(nameof(SourceDocumentId))]
    public ProjectDocument? SourceDocument { get; set; }
}

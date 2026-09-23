using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ToDo.Entities;

public enum ProjectDocumentIndexStatus
{
    Pending = 0,
    Processing = 1,
    Ready = 2,
    Failed = 3,
    Unsupported = 4
}

[Table("project_documents")]
public class ProjectDocument
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int UploadedById { get; set; }
    public ProjectDocumentCategory Category { get; set; } = ProjectDocumentCategory.Other;

    public int? CategoryId { get; set; }

    [MaxLength(50)]
    public string? CategoryName { get; set; }

    [Required, MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string StoragePath { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? FileHash { get; set; }

    public long FileSize { get; set; }
    public int VersionNumber { get; set; } = 1;
    public bool IsCurrent { get; set; } = true;
    public bool ConflictDetected { get; set; }
    public int? ConflictWithDocumentId { get; set; }
    public string? Description { get; set; }
    public DateTime UploadedAt { get; set; } = AppTime.Now;

    public ProjectDocumentIndexStatus IndexStatus { get; set; } = ProjectDocumentIndexStatus.Pending;
    public int IndexAttemptCount { get; set; }
    public DateTime? IndexLockedAt { get; set; }
    public DateTime? IndexNextRetryAt { get; set; }
    public DateTime? IndexedAt { get; set; }

    [MaxLength(2000)]
    public string IndexError { get; set; } = string.Empty;

    public int ExtractedCharacterCount { get; set; }

    public ICollection<ProjectDocumentChunk> Chunks { get; set; } = new List<ProjectDocumentChunk>();
}

[Table("project_document_chunks")]
public class ProjectDocumentChunk
{
    public long Id { get; set; }
    public int ProjectDocumentId { get; set; }
    public int ProjectId { get; set; }
    public int ChunkIndex { get; set; }

    [Column(TypeName = "longtext")]
    public string Content { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Heading { get; set; } = string.Empty;

    public int CharacterCount { get; set; }
    public DateTime CreatedAt { get; set; } = AppTime.Now;

    [ForeignKey(nameof(ProjectDocumentId))]
    public ProjectDocument? Document { get; set; }
}

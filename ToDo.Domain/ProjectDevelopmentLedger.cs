using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public sealed class ProjectHistoryParticipant
{
    public string Name { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Responsibility { get; set; } = "";
}

public sealed class ProjectGitChange
{
    public string Commit { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTimeOffset AuthoredAt { get; set; }
    public DateTimeOffset CommittedAt { get; set; }
    public string Subject { get; set; } = "";
    public string Message { get; set; } = "";
    public int FileCount { get; set; }
    public List<string> Files { get; set; } = [];
}

/// <summary>Git evidence is not a historical database audit log, and does not assign account permissions.</summary>
public sealed class ProjectDevelopmentLedger
{
    public const string FileName = "project-development-ledger.json";
    public const int MaximumBytes = 8 * 1024 * 1024;
    public int FormatVersion { get; set; } = 1;
    public int ProjectId { get; set; }
    public string SourceHead { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
    public List<ProjectHistoryParticipant> Participants { get; set; } = [];
    public List<ProjectGitChange> Changes { get; set; } = [];
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static ProjectDevelopmentLedger Parse(string json, int expectedProjectId)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new ArgumentException("开发记录文件过大");
        ProjectDevelopmentLedger ledger;
        try { ledger = JsonSerializer.Deserialize<ProjectDevelopmentLedger>(json, JsonOptions) ?? throw new ArgumentException("开发记录为空"); }
        catch (JsonException) { throw new ArgumentException("开发记录 JSON 无效"); }
        if (ledger.FormatVersion != 1 || ledger.ProjectId != expectedProjectId || ledger.CapturedAt == default
            || !Hash(ledger.SourceHead) || ledger.Changes is null || ledger.Changes.Count is < 1 or > 5000
            || ledger.Participants is null || ledger.Participants.Count > 50) throw new ArgumentException("开发记录版本、项目或数量无效");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ledger.Changes)
            if (c is null || !Hash(c.Commit) || !keys.Add(c.Commit) || !Text(c.Author, 1, 160) || !Text(c.Subject, 1, 1000)
                || !Text(c.Message, 0, 8000) || c.AuthoredAt == default || c.CommittedAt == default || c.FileCount < 0
                || c.Files is null || c.Files.Count > 100 || c.FileCount < c.Files.Count || c.Files.Any(f => !Text(f, 1, 1000)))
                throw new ArgumentException("开发记录字段无效或提交重复");
        if (ledger.Participants.Any(p => p is null || !Text(p.Name, 1, 80) || !Text(p.UserName, 1, 80) || !Text(p.Responsibility, 0, 500))
            || ledger.Participants.Select(p => p.UserName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ledger.Participants.Count)
            throw new ArgumentException("参与人员字段无效或重复");
        return ledger;
    }
    private static bool Hash(string? value) => value is not null && Regex.IsMatch(value, "^[a-fA-F0-9]{40}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static bool Text(string? value, int min, int max) => value is not null && value.Length >= min && value.Length <= max;
}

public sealed class ProjectDevelopmentLedgerReader(ApplicationDbContext context, ProjectDocumentService documents)
{
    public async Task<(ProjectDevelopmentLedger Ledger, ProjectDocument Document)?> ReadAsync(int projectId, int userId, CancellationToken ct = default)
    {
        var actor = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && !u.IsDeleted && u.Status == UserStatus.Active, ct);
        if (actor is null || !await ProjectArchiveAccess.CanReadAsync(context, projectId, userId, actor.Role, ct))
            throw new UnauthorizedAccessException("无权查看该项目开发记录");
        var document = await context.ProjectDocuments.AsNoTracking().Where(d => d.ProjectId == projectId && d.IsCurrent && d.FileName == ProjectDevelopmentLedger.FileName)
            .OrderByDescending(d => d.VersionNumber).ThenByDescending(d => d.Id).FirstOrDefaultAsync(ct);
        if (document is null) return null;
        var resolved = await documents.ResolveDownloadAsync(document.Id, projectId, ct);
        if (resolved is null || new FileInfo(resolved.Value.FullPath).Length > ProjectDevelopmentLedger.MaximumBytes)
            throw new ArgumentException("开发记录原件缺失或超过大小限制");
        var bytes = await File.ReadAllBytesAsync(resolved.Value.FullPath, ct);
        if (bytes.Length > ProjectDevelopmentLedger.MaximumBytes) throw new ArgumentException("开发记录原件过大");
        if (!string.IsNullOrWhiteSpace(document.FileHash) && !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), document.FileHash, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("开发记录原件与已保存的哈希不符，请检查运行文件");
        return (ProjectDevelopmentLedger.Parse(Encoding.UTF8.GetString(bytes), projectId), document);
    }
}

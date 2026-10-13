using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ToDo.Domain;

public sealed class ProjectArchiveRecord
{
    public string Key { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string DateLabel { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string SourceLabel { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
}

/// <summary>Read-only snapshots stored as a versioned project document, never as live tasks.</summary>
public sealed class ProjectArchivePackage
{
    public const string FileName = "project-history-archive.json";
    public const int MaximumBytes = 8 * 1024 * 1024;
    public int FormatVersion { get; set; }
    public int ProjectId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public List<ProjectArchiveRecord> Records { get; set; } = [];

    public static string HashBody(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    public static ProjectArchivePackage Parse(string json, int expectedProjectId)
    {
        if (expectedProjectId <= 0 || string.IsNullOrWhiteSpace(json)
            || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new ArgumentException("历史档案文件大小或项目无效");
        ProjectArchivePackage? package;
        try { package = JsonSerializer.Deserialize<ProjectArchivePackage>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { throw new ArgumentException("历史档案 JSON 格式无效"); }
        if (package is null || package.FormatVersion != 1 || package.ProjectId != expectedProjectId
            || package.RecordedAt == default || package.Records is null || package.Records.Count is < 1 or > 500)
            throw new ArgumentException("历史档案版本、所属项目或记录数量无效");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in package.Records)
        {
            if (record is null || record.Key is null || !Regex.IsMatch(record.Key, "^[a-z0-9][a-z0-9._:-]{1,79}$",
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) || !keys.Add(record.Key))
                throw new ArgumentException("历史档案记录编号无效或重复");
            if (record.Kind is not ("task" or "meeting") || !Valid(record.Title, 1, 255)
                || !Valid(record.DateLabel, 0, 100) || !Valid(record.Owner, 0, 200)
                || !Valid(record.Status, 0, 100) || !Valid(record.SourceLabel, 1, 1000)
                || !Valid(record.Body, 1, 250_000) || !Valid(record.SourceUrl, 0, 200))
                throw new ArgumentException("历史档案记录字段无效");
            var route = record.Kind == "task" ? "Tasks" : "MeetingMinutes";
            if (record.SourceUrl.Length > 0 && !Regex.IsMatch(record.SourceUrl,
                    $"^/{route}/Details/[1-9][0-9]*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                throw new ArgumentException("历史档案来源链接必须指向本站对应记录");
            if (!string.Equals(HashBody(record.Body), record.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("历史档案正文校验失败");
        }
        return package;
    }

    private static bool Valid(string? text, int minimum, int maximum)
        => text is not null && text.Length >= minimum && text.Length <= maximum;
}

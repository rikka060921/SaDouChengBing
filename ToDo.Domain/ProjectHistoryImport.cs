using System.Text.Json;
using System.Text.RegularExpressions;

namespace ToDo.Domain;

public sealed class ProjectHistoryImportItem
{
    public string Key { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public DateTime SortDate { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Sources { get; set; } = string.Empty;
}

/// <summary>Validate retrospective records before any database write.</summary>
public static class ProjectHistoryImport
{
    private static readonly Regex KeyPattern = new("^[a-z0-9][a-z0-9._:-]{1,79}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static IReadOnlyList<ProjectHistoryImportItem> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 50_000)
            throw new ArgumentException("请提供不超过 50,000 字符的历史节点 JSON");

        List<ProjectHistoryImportItem>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<ProjectHistoryImportItem>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            throw new ArgumentException("历史节点 JSON 格式无效");
        }

        if (items is null || items.Count is < 1 or > 50 || items.Any(item => item is null))
            throw new ArgumentException("每次须导入 1 至 50 条历史节点");

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            item.Key = item.Key?.Trim() ?? string.Empty;
            item.Period = item.Period?.Trim() ?? string.Empty;
            item.Title = item.Title?.Trim() ?? string.Empty;
            item.Summary = item.Summary?.Trim() ?? string.Empty;
            item.Sources = item.Sources?.Trim() ?? string.Empty;
            if (!KeyPattern.IsMatch(item.Key) || !keys.Add(item.Key))
                throw new ArgumentException("历史节点 Key 无效或在本批次重复");
            if (item.Period.Length is < 4 or > 32 || item.Title.Length is < 2 or > 160
                || item.Summary.Length is < 2 or > 2000 || item.Sources.Length is < 2 or > 1000)
                throw new ArgumentException($"历史节点 {item.Key} 的时间、标题、说明或来源长度无效");
            if (item.SortDate.Date < new DateTime(1900, 1, 1) || item.SortDate.Date > DateTime.Today)
                throw new ArgumentException($"历史节点 {item.Key} 的排序日期无效");
        }
        return items;
    }
}

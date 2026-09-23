using System.Text.Json;

namespace ToDo.Domain.AI;

public static class TaskSplitJsonParser
{
    public static bool TryParse(string response, out JsonElement items, out bool wasTruncated)
    {
        items = default;
        wasTruncated = false;
        var start = response.IndexOf('[');
        if (start < 0) return false;
        var array = response[start..];
        var end = array.LastIndexOf(']');
        try
        {
            using var document = JsonDocument.Parse(end >= 0 ? array[..(end + 1)] : array);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            items = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            // 仅保留截断前完整的任务对象，不补造半条任务的标题或字段。
            if (!AIService.TryRepairTruncatedJson("{\"items\":" + array, out var repaired)) return false;
            using var document = JsonDocument.Parse(repaired);
            if (!document.RootElement.TryGetProperty("items", out var result) || result.ValueKind != JsonValueKind.Array) return false;
            items = result.Clone();
            wasTruncated = true;
            return true;
        }
    }
}

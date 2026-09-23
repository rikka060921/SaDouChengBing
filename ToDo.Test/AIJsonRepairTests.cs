using System.Text.Json;
using ToDo.Domain.AI;

namespace ToDo.Test;

public sealed class AIJsonRepairTests
{
    [Fact]
    public void TryRepairTruncatedJson_WhenNextArrayObjectIsIncomplete_DropsOnlyIncompleteObject()
    {
        const string truncated = """
            {"Decisions":[{"content":"采用方案 A"},{"content":"尚未输出完成
            """;

        var success = AIService.TryRepairTruncatedJson(truncated, out var repaired);

        Assert.True(success);
        using var document = JsonDocument.Parse(repaired);
        var decisions = document.RootElement.GetProperty("Decisions");
        Assert.Equal(1, decisions.GetArrayLength());
        Assert.Equal("采用方案 A", decisions[0].GetProperty("content").GetString());
    }

    [Fact]
    public void TryRepairTruncatedJson_WhenLaterRootPropertyIsIncomplete_PreservesCompletedProperties()
    {
        const string truncated = """
            {"Decisions":[{"content":"采用方案 A"}],"TaskItems":[{"title":"尚未输出完成
            """;

        var success = AIService.TryRepairTruncatedJson(truncated, out var repaired);

        Assert.True(success);
        using var document = JsonDocument.Parse(repaired);
        Assert.True(document.RootElement.TryGetProperty("Decisions", out var decisions));
        Assert.Equal(1, decisions.GetArrayLength());
        Assert.False(document.RootElement.TryGetProperty("TaskItems", out _));
    }

    [Fact]
    public void TryRepairTruncatedJson_WhenIncompleteObjectContainsNestedArray_UsesSafeBoundaryStack()
    {
        const string truncated = """
            {"Decisions":[{"content":"采用方案 A","originalQuotes":["原话一"]},{"content":"方案 B","originalQuotes":["未完成
            """;

        var success = AIService.TryRepairTruncatedJson(truncated, out var repaired);

        Assert.True(success);
        using var document = JsonDocument.Parse(repaired);
        var decisions = document.RootElement.GetProperty("Decisions");
        Assert.Equal(1, decisions.GetArrayLength());
        Assert.Equal("原话一", decisions[0].GetProperty("originalQuotes")[0].GetString());
    }

    [Fact]
    public void TryRepairTruncatedJson_WhenNoCompleteValueExists_ReturnsFalseWithoutFabricatingData()
    {
        const string truncated = """
            {"Decisions":[{"content":"第一条也没有完成
            """;

        var success = AIService.TryRepairTruncatedJson(truncated, out var repaired);

        Assert.False(success);
        Assert.Empty(repaired);
    }

    [Fact]
    public void TryRepairTruncatedJson_WhenBracketsAreMismatched_ReturnsFalse()
    {
        const string malformed = "{\"Decisions\":[{\"content\":\"A\"]";

        var success = AIService.TryRepairTruncatedJson(malformed, out var repaired);

        Assert.False(success);
        Assert.Empty(repaired);
    }
}

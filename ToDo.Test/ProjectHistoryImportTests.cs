using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;
using Xunit;

namespace ToDo.Test;

public sealed class ProjectHistoryImportTests
{
    [Fact]
    public void Parse_accepts_a_sourced_retrospective_milestone()
    {
        var items = ProjectHistoryImport.Parse("""
            [{"key":"phase-2025-07","period":"2025-07","sortDate":"2025-07-16",
              "title":"项目雏形","summary":"首个提交和数据库迁移进入仓库。",
              "sources":"Git 5a02987、a564107"}]
            """);

        var item = Assert.Single(items);
        Assert.Equal("phase-2025-07", item.Key);
        Assert.Equal(new DateTime(2025, 7, 16), item.SortDate);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("[{\"key\":\"bad key\",\"period\":\"2025-07\",\"sortDate\":\"2025-07-16\",\"title\":\"标题\",\"summary\":\"说明\",\"sources\":\"Git abc\"}]")]
    [InlineData("[{\"key\":\"phase-a\",\"period\":\"2025-07\",\"sortDate\":\"2025-07-16\",\"title\":\"标题\",\"summary\":\"说明\",\"sources\":\"\"}]")]
    public void Parse_rejects_invalid_batches(string json)
        => Assert.Throws<ArgumentException>(() => ProjectHistoryImport.Parse(json));

    [Fact]
    public void Parse_rejects_duplicate_keys_in_same_batch()
    {
        const string item = """{"key":"phase-a","period":"2025-07","sortDate":"2025-07-16","title":"标题","summary":"说明","sources":"Git abc"}""";
        Assert.Throws<ArgumentException>(() => ProjectHistoryImport.Parse($"[{item},{item}]"));
    }

    [Fact]
    public void Model_has_project_scoped_unique_source_key()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var context = new ApplicationDbContext(options);
        var index = Assert.Single(context.Model.FindEntityType(typeof(ProjectHistoryEntry))!.GetIndexes(),
            item => item.IsUnique);
        Assert.Equal([nameof(ProjectHistoryEntry.ProjectId), nameof(ProjectHistoryEntry.SourceKey)],
            index.Properties.Select(property => property.Name));
    }
}

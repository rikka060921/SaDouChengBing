using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;
using Xunit;

namespace ToDo.Test;

public sealed class ProjectArchivePackageTests
{
    [Theory]
    [InlineData(1, UserRole.teamMember, true)]
    [InlineData(2, UserRole.teamMember, true)]
    [InlineData(3, UserRole.teamMember, false)]
    [InlineData(3, UserRole.systemAdmin, true)]
    [InlineData(0, UserRole.systemAdmin, false)]
    public async Task Public_project_overview_does_not_grant_archive_access(int userId, UserRole role, bool expected)
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        context.Project.Add(new Project { Id = 20, Name = "公开项目", LeaderUserId = 1, CreatedByUserId = 1, IsEncrypted = '0' });
        context.ProjectUsers.Add(new ProjectUser { ProjectId = 20, UserId = 2 });
        await context.SaveChangesAsync();
        Assert.Equal(expected, await ProjectArchiveAccess.CanReadAsync(context, 20, userId, role));
    }

    [Fact]
    public void Parse_preserves_original_task_status_and_text_without_creating_a_live_task()
    {
        var package = Example();
        package.Records[0].Status = "待审核";
        package.Records[0].Body = "原记录含 <script> 标签时仍是档案文本。";
        package.Records[0].ContentHash = ProjectArchivePackage.HashBody(package.Records[0].Body);
        var parsed = ProjectArchivePackage.Parse(JsonSerializer.Serialize(package), 20);
        Assert.Equal("待审核", Assert.Single(parsed.Records).Status);
        Assert.Equal(package.Records[0].Body, parsed.Records[0].Body);
    }

    [Fact]
    public void Parse_rejects_a_package_belonging_to_another_project()
        => Assert.Throws<ArgumentException>(() => ProjectArchivePackage.Parse(JsonSerializer.Serialize(Example()), 21));

    [Fact]
    public void Parse_rejects_modified_body_without_a_matching_hash()
    {
        var package = Example();
        package.Records[0].Body += "被修改";
        Assert.Throws<ArgumentException>(() => ProjectArchivePackage.Parse(JsonSerializer.Serialize(package), 20));
    }

    [Theory]
    [InlineData("https://example.com/Tasks/Details/102")]
    [InlineData("//example.com/Tasks/Details/102")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/Tasks/Details/102?redirect=elsewhere")]
    [InlineData("/MeetingMinutes/Details/102")]
    public void Parse_rejects_untrusted_or_wrong_record_source_links(string url)
    {
        var package = Example();
        package.Records[0].SourceUrl = url;
        Assert.Throws<ArgumentException>(() => ProjectArchivePackage.Parse(JsonSerializer.Serialize(package), 20));
    }

    [Fact]
    public void Parse_rejects_duplicate_source_keys()
    {
        var package = Example();
        package.Records.Add(package.Records[0]);
        Assert.Throws<ArgumentException>(() => ProjectArchivePackage.Parse(JsonSerializer.Serialize(package), 20));
    }

    [Fact]
    public void Parse_allows_local_meeting_notes_without_inventing_a_source_url_or_date()
    {
        var package = Example();
        var record = package.Records[0];
        record.Kind = "meeting";
        record.SourceUrl = string.Empty;
        record.DateLabel = string.Empty;
        record.SourceLabel = "本地会议总结；会议日期尚未核实";
        var parsed = ProjectArchivePackage.Parse(JsonSerializer.Serialize(package), 20);
        Assert.Empty(parsed.Records[0].DateLabel);
        Assert.Empty(parsed.Records[0].SourceUrl);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"FormatVersion\":1,\"ProjectId\":20,\"Records\":null}")]
    public void Parse_rejects_invalid_packages(string json)
        => Assert.Throws<ArgumentException>(() => ProjectArchivePackage.Parse(json, 20));

    private static ProjectArchivePackage Example() => new()
    {
        FormatVersion = 1,
        ProjectId = 20,
        RecordedAt = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero),
        Records = [new ProjectArchiveRecord
        {
            Key = "old14-task102", Kind = "task", Title = "梳理会议纪要",
            Status = "进行中", SourceLabel = "旧项目 #14，任务 #102",
            SourceUrl = "/Tasks/Details/102", Body = "原任务说明",
            ContentHash = ProjectArchivePackage.HashBody("原任务说明")
        }]
    };
}

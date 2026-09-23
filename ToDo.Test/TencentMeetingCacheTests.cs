using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ToDo.Domain;
using ToDo.Domain.Options;

namespace ToDo.Test;

public sealed class TencentMeetingCacheTests
{
    [Fact]
    public async Task Refresh_QueriesNewMeetings_AndFallsBackOnlyWhenOffline()
    {
        using var cli = new FakeCli();
        var day = new DateTime(2026, 9, 12);
        Assert.Single(await cli.QueryDailyMeetListAsync(day));
        cli.Meetings = 2;
        Assert.Equal(2, (await cli.QueryDailyMeetListAsync(day)).Count);
        Assert.Equal(2, cli.ListCalls);
        cli.LoggedIn = false;
        Assert.Equal(2, (await cli.QueryDailyMeetListAsync(day)).Count);
        cli.LoggedIn = true;
        cli.Meetings = 3;
        Assert.Equal(3, (await cli.QueryDailyMeetListAsync(day)).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"error\":\"unavailable\"}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":{\"meeting_info_list\":null}}")]
    [InlineData("{\"data\":{\"meeting_info_list\":[null]}}")]
    [InlineData("{\"data\":{\"meeting_info_list\":[{\"meeting_id\":123}]}}")]
    public async Task InvalidListResponse_PreservesCachedMeetings(string output)
    {
        using var cli = new FakeCli();
        var day = new DateTime(2026, 9, 12);
        Assert.Single(await cli.QueryDailyMeetListAsync(day));
        cli.ListOutput = output;
        Assert.Single(await cli.QueryDailyMeetListAsync(day));
        cli.LoggedIn = false;
        Assert.Single(await cli.QueryDailyMeetListAsync(day));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"data\":null}")]
    public async Task InvalidListWithoutCache_ReturnsEmptyList(string output)
    {
        using var cli = new FakeCli { ListOutput = output };
        Assert.Empty(await cli.QueryDailyMeetListAsync(new DateTime(2026, 9, 12)));
    }

    [Fact]
    public async Task ValidEmptyList_ReplacesOldCache()
    {
        using var cli = new FakeCli();
        var day = new DateTime(2026, 9, 12);
        Assert.Single(await cli.QueryDailyMeetListAsync(day));
        cli.Meetings = 0;
        Assert.Empty(await cli.QueryDailyMeetListAsync(day));
        cli.LoggedIn = false;
        Assert.Empty(await cli.QueryDailyMeetListAsync(day));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":{\"minutes\":[]}}")]
    [InlineData("{\"data\":{\"minutes\":{\"paragraphs\":[null]}}}")]
    [InlineData("{\"data\":{\"minutes\":{\"paragraphs\":[{\"speaker\":null}]}}}")]
    [InlineData("{\"data\":{\"minutes\":{\"paragraphs\":[{\"sentences\":[{\"words\":[{\"text\":42}]}]}]}}}")]
    public async Task InvalidTranscriptShape_PreservesCacheAndRecoversOnNextRefresh(string output)
    {
        using var cli = new FakeCli { Transcript = "真实缓存转写" };
        var original = await cli.FetchSingleMeetTranscriptAsync("record-1");
        cli.TranscriptOutput = output;
        Assert.Equal(original.CleanText, (await cli.FetchSingleMeetTranscriptAsync("record-1")).CleanText);
        cli.LoggedIn = false;
        Assert.Equal(original.RawJson, (await cli.FetchSingleMeetTranscriptAsync("record-1")).RawJson);
        cli.LoggedIn = true;
        cli.TranscriptOutput = null;
        cli.Transcript = "恢复后的新转写";
        Assert.Contains("恢复后的新转写", (await cli.FetchSingleMeetTranscriptAsync("record-1")).CleanText);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"data\":null}")]
    public async Task InvalidTranscriptWithoutCache_ReturnsFriendlyError(string output)
    {
        using var cli = new FakeCli { TranscriptOutput = output };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => cli.FetchSingleMeetTranscriptAsync("record-1"));
        Assert.Contains("会议转写尚未生成或返回格式无效", error.Message);
    }

    [Fact]
    public async Task TranscriptRefresh_UpdatesText_AndDoesNotCacheEmptyResponses()
    {
        using var cli = new FakeCli();
        cli.Transcript = "初版转写";
        Assert.Contains("初版转写", (await cli.FetchSingleMeetTranscriptAsync("record-1")).CleanText);
        cli.Transcript = "修订后的完整转写";
        Assert.Contains("修订后的完整转写", (await cli.FetchSingleMeetTranscriptAsync("record-1")).CleanText);
        cli.Transcript = "";
        Assert.Contains("修订后的完整转写", (await cli.FetchSingleMeetTranscriptAsync("record-1")).CleanText);
        cli.LoggedIn = false;
        Assert.Contains("修订后的完整转写", (await cli.FetchSingleMeetTranscriptAsync("record-1")).CleanText);
    }

    private sealed class FakeCli : TencentMeetingLocalCliService, IDisposable
    {
        private readonly string _directory;
        public bool LoggedIn { get; set; } = true;
        public int Meetings { get; set; } = 1;
        public int ListCalls { get; private set; }
        public string? ListOutput { get; set; }
        public string? TranscriptOutput { get; set; }
        public string Transcript { get; set; } = "转写内容";
        public FakeCli() : this(Path.Combine(Path.GetTempPath(), "todo-cache-test-" + Guid.NewGuid().ToString("N"))) { }
        private FakeCli(string directory) : base(Options.Create(new MeetingCliOptions { MeetingCacheRoot = directory }), NullLogger<TencentMeetingLocalCliService>.Instance) => _directory = directory;
        internal override Task<string> ExecuteCliCommandAsync(List<string> args, bool throwOnError = true)
        {
            if (args[0] == "auth") return Task.FromResult(LoggedIn ? "Logged in" : "Not authenticated");
            if (args[0] == "meeting")
            {
                ListCalls++;
                return Task.FromResult(ListOutput ?? JsonSerializer.Serialize(new { data = new { meeting_info_list = Enumerable.Range(1, Meetings).Select(i => new { meeting_id = $"meeting-{i}", subject = $"meeting-{i}", start_time = "2026-09-12T10:00:00+08:00", end_time = "2026-09-12T11:00:00+08:00" }) } }));
            }
            if (args[1] == "transcript-get") return Task.FromResult(TranscriptOutput ?? JsonSerializer.Serialize(new { data = new { minutes = new { paragraphs = new[] { new { speaker = new { user_name = "测试人员" }, sentences = new[] { new { words = new[] { new { text = Transcript } } } } } } } } }));
            return Task.FromResult("{\"data\":{\"record_meetings\":[{\"record_files\":[{\"record_file_id\":\"record-1\"}]}]}}");
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}

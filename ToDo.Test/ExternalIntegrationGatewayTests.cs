using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.Dto;
using ToDo.Domain.Options;

namespace ToDo.Test;

public sealed class ExternalIntegrationGatewayTests
{
    [Fact]
    public async Task TencentTranscriptService_MissingToken_DegradesUntilFeatureIsInvoked()
    {
        var originalToken = Environment.GetEnvironmentVariable("TENCENT_MEETING_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("TENCENT_MEETING_TOKEN", null);
            var service = new TencentMeetingMcpService(
                new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, "{}"))),
                Options.Create(new TencentMeetingOptions()),
                NullLogger<TencentMeetingMcpService>.Instance);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.QueryDailyMeetListAsync(DateTime.Today));

            Assert.Contains("TENCENT_MEETING_TOKEN", exception.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TENCENT_MEETING_TOKEN", originalToken);
        }
    }

    [Fact]
    public async Task WeComSend_UsesRealEndpoint_RedactsAuditAndHonorsIdempotency()
    {
        await using var context = CreateContext();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("gettoken")
            ? Json(HttpStatusCode.OK, "{\"errcode\":0,\"errmsg\":\"ok\",\"access_token\":\"token-value\"}")
            : Json(HttpStatusCode.OK, "{\"errcode\":0,\"errmsg\":\"ok\",\"msgid\":\"message-1\"}"));
        var gateway = new WeComGateway(context, new HttpClient(handler), Options.Create(new WeComOptions
        {
            Enabled = true,
            CorpId = "corp-id",
            AgentId = "100001",
            Secret = "secret-value",
            ApiBaseUrl = "https://qyapi.weixin.qq.com"
        }));

        var first = await gateway.SendTextAsync("zhangsan", "机密项目已完成", idempotencyKey: "same-request");
        var second = await gateway.SendTextAsync("zhangsan", "机密项目已完成", idempotencyKey: "same-request");

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(2, handler.CallCount);
        var record = await context.IntegrationCallRecords.SingleAsync();
        Assert.True(record.IsSuccess);
        Assert.Equal("same-request", record.IdempotencyKey);
        Assert.DoesNotContain("机密项目已完成", record.RequestJson);
        Assert.DoesNotContain("zhangsan", record.RequestJson);
        Assert.DoesNotContain("secret-value", record.RequestJson);
    }

    [Fact]
    public async Task TencentCreateMeeting_SignsRequestAndDoesNotPersistSubject()
    {
        await using var context = CreateContext();
        HttpRequestMessage? captured = null;
        string body = string.Empty;
        var handler = new StubHandler(async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, "{\"meeting_number\":1,\"meeting_info_list\":[{\"meeting_id\":\"meeting-1\",\"start_time\":\"1\",\"end_time\":\"2\",\"join_url\":\"private\",\"password\":\"1234\"}]}");
        });
        var options = new TencentMeetingOptions
        {
            Enabled = true,
            AppId = "app-id",
            SdkId = "sdk-id",
            SecretId = "secret-id",
            SecretKey = "secret-key",
            DefaultUserId = "creator-id",
            ApiBaseUrl = "https://api.meeting.qq.com"
        };
        var gateway = new TencentMeetingGateway(context, new HttpClient(handler), new FakeLocalMeeting(), Options.Create(options));

        var result = await gateway.CreateMeetingAsync("董事会机密会议", DateTime.Now.AddHours(1), DateTime.Now.AddHours(2), idempotencyKey: "meeting-key");

        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.Equal("1", captured!.Headers.GetValues("X-TC-Registered").Single());
        var nonce = captured.Headers.GetValues("X-TC-Nonce").Single();
        var timestamp = captured.Headers.GetValues("X-TC-Timestamp").Single();
        var source = $"POST\nX-TC-Key=secret-id&X-TC-Nonce={nonce}&X-TC-Timestamp={timestamp}\n/v1/meetings\n{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("secret-key"));
        var hex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(hex));
        Assert.Equal(expected, captured.Headers.GetValues("X-TC-Signature").Single());
        var record = await context.IntegrationCallRecords.SingleAsync();
        Assert.DoesNotContain("董事会机密会议", record.RequestJson);
        Assert.DoesNotContain("join_url", record.ResponseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", record.ResponseJson, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 60)]
    [InlineData(10, 60)]
    public void ScheduledJobRetryDelay_HasBoundedBackoff(int failures, int minutes)
    {
        var method = typeof(ScheduledJobService).GetMethod("GetFailureRetryDelay", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var result = (TimeSpan)method.Invoke(null, new object[] { failures })!;
        Assert.Equal(TimeSpan.FromMinutes(minutes), result);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this(request => Task.FromResult(handler(request))) { }

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return _handler(request);
        }
    }

    private sealed class FakeLocalMeeting : ITencentMeetingLocalCliService
    {
        public Task<List<TencentMeetDailyItem>> QueryDailyMeetListAsync(DateTime meetDate) => Task.FromResult(new List<TencentMeetDailyItem>());
        public Task<TencentMeetTranscriptResult> FetchSingleMeetTranscriptAsync(string recordFileId, string? title = null) =>
            Task.FromResult(new TencentMeetTranscriptResult { RecordId = recordFileId, Title = title ?? recordFileId, CleanText = "转写", RawJson = "{}" });
        public Task<(string MergedCleanContent, string AllRawJson, string RecordIdsJoin)> BatchFetchAndMergeAsync(List<string> recordFileIds, Dictionary<string, string>? titleMap = null) =>
            Task.FromResult((string.Empty, string.Empty, string.Join(',', recordFileIds)));
    }
}

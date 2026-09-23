using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToDo.Context;
using ToDo.Domain.Options;
using ToDo.Entities;

namespace ToDo.Domain;

public record IntegrationResult(bool Success, string Status, string Message, object? Data = null);

public interface IWeComGateway
{
    Task<IntegrationResult> SendTextAsync(string recipient, string content, CancellationToken cancellationToken = default, string? idempotencyKey = null);
    Task<IntegrationResult> GetVoiceMessageAsync(string messageId, CancellationToken cancellationToken = default, string? idempotencyKey = null);
}

public interface ITencentMeetingGateway
{
    Task<IntegrationResult> CreateMeetingAsync(string subject, DateTime startAt, DateTime endAt, CancellationToken cancellationToken = default, string? idempotencyKey = null);
    Task<IntegrationResult> GetTranscriptAsync(string meetingId, CancellationToken cancellationToken = default, string? idempotencyKey = null);
}

public sealed class WeComGateway : IWeComGateway
{
    private readonly ApplicationDbContext _context;
    private readonly HttpClient _httpClient;
    private readonly WeComOptions _options;

    public WeComGateway(
        ApplicationDbContext context,
        HttpClient httpClient,
        IOptions<WeComOptions> options)
    {
        _context = context;
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 3, 60));
    }

    public async Task<IntegrationResult> SendTextAsync(
        string recipient,
        string content,
        CancellationToken cancellationToken = default,
        string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(recipient)) throw new ArgumentException("企业微信接收对象不能为空", nameof(recipient));
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("企业微信消息不能为空", nameof(content));
        if (content.Length > 2048) throw new ArgumentException("企业微信文本消息不能超过 2048 个字符", nameof(content));
        var requestAudit = new
        {
            recipient = MaskIdentifier(recipient),
            contentLength = content.Length,
            contentSha256 = Sha256(content)
        };
        var key = NormalizeIdempotencyKey(idempotencyKey);
        var existing = await FindSucceededAsync("WeCom", "SendText", key, cancellationToken);
        if (existing != null) return new IntegrationResult(true, "Succeeded", "相同幂等请求已成功处理", new { existing.Id });
        if (!IsConfigured()) return await RecordAsync("SendText", requestAudit, key, false, "NotConfigured", "企业微信尚未配置", 0, 0, null, "Configuration", cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        try
        {
            var token = await GetAccessTokenAsync(cancellationToken);
            var payload = JsonSerializer.Serialize(new
            {
                touser = recipient.Trim(),
                msgtype = "text",
                agentid = _options.AgentId,
                text = new { content = content.Trim() },
                enable_duplicate_check = 1,
                duplicate_check_interval = 1800
            });
            var response = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{BaseUrl()}/cgi-bin/message/send?access_token={Uri.EscapeDataString(token)}");
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                return request;
            }, cancellationToken, count => attempts = count);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            var api = ParseWeComResponse(responseText);
            var success = response.IsSuccessStatusCode && api.ErrorCode == 0;
            var status = success ? "Succeeded" : "Rejected";
            var message = success ? "企业微信消息发送成功" : $"企业微信拒绝请求：{api.ErrorMessage}";
            return await RecordAsync("SendText", requestAudit, key, success, status, message,
                attempts, (int)stopwatch.ElapsedMilliseconds, (int)response.StatusCode,
                success ? string.Empty : "Provider", cancellationToken,
                new { api.errcode, api.errmsg, api.msgid });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return await RecordAsync("SendText", requestAudit, key, false, "Failed", "企业微信调用失败",
                Math.Max(attempts, 1), (int)stopwatch.ElapsedMilliseconds, null,
                ex is TaskCanceledException ? "Timeout" : "Transport", cancellationToken,
                new { error = SafeError(ex) });
        }
    }

    public async Task<IntegrationResult> GetVoiceMessageAsync(
        string messageId,
        CancellationToken cancellationToken = default,
        string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("企业微信媒体 ID 不能为空", nameof(messageId));
        var requestAudit = new { mediaId = MaskIdentifier(messageId) };
        var key = NormalizeIdempotencyKey(idempotencyKey);
        if (!IsConfigured()) return await RecordAsync("GetVoiceMessage", requestAudit, key, false, "NotConfigured", "企业微信尚未配置", 0, 0, null, "Configuration", cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        try
        {
            var token = await GetAccessTokenAsync(cancellationToken);
            var response = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get,
                    $"{BaseUrl()}/cgi-bin/media/get?access_token={Uri.EscapeDataString(token)}&media_id={Uri.EscapeDataString(messageId.Trim())}"),
                cancellationToken,
                count => attempts = count);
            var media = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            if (!response.IsSuccessStatusCode || contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                var providerError = Encoding.UTF8.GetString(media);
                return await RecordAsync("GetVoiceMessage", requestAudit, key, false, "Rejected", "企业微信媒体获取失败",
                    attempts, (int)stopwatch.ElapsedMilliseconds, (int)response.StatusCode, "Provider", cancellationToken,
                    ParseSafeJson(providerError));
            }

            var data = new { Bytes = media, ContentType = contentType, FileName = response.Content.Headers.ContentDisposition?.FileNameStar };
            return await RecordAsync("GetVoiceMessage", requestAudit, key, true, "Succeeded", "企业微信语音获取成功",
                attempts, (int)stopwatch.ElapsedMilliseconds, (int)response.StatusCode, string.Empty, cancellationToken,
                new { byteLength = media.Length, contentType }, data);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return await RecordAsync("GetVoiceMessage", requestAudit, key, false, "Failed", "企业微信调用失败",
                Math.Max(attempts, 1), (int)stopwatch.ElapsedMilliseconds, null, "Transport", cancellationToken,
                new { error = SafeError(ex) });
        }
    }

    private bool IsConfigured() => _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.CorpId)
        && !string.IsNullOrWhiteSpace(_options.AgentId)
        && !string.IsNullOrWhiteSpace(_options.Secret);

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var url = $"{BaseUrl()}/cgi-bin/gettoken?corpid={Uri.EscapeDataString(_options.CorpId)}&corpsecret={Uri.EscapeDataString(_options.Secret)}";
        using var response = await client.GetAsync(url, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var api = ParseWeComResponse(json);
        if (!response.IsSuccessStatusCode || api.ErrorCode != 0 || string.IsNullOrWhiteSpace(api.AccessToken))
            throw new HttpRequestException($"企业微信 access_token 获取失败：{api.ErrorMessage}");
        return api.AccessToken;
    }

    private HttpClient CreateClient()
    {
        return _httpClient;
    }

    private string BaseUrl() => (_options.ApiBaseUrl ?? string.Empty).TrimEnd('/');

    private static WeComApiResponse ParseWeComResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new WeComApiResponse(
            root.TryGetProperty("errcode", out var code) && code.TryGetInt32(out var value) ? value : -1,
            root.TryGetProperty("errmsg", out var error) ? error.GetString() ?? "未知错误" : "响应格式错误",
            root.TryGetProperty("access_token", out var token) ? token.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("msgid", out var messageId) ? messageId.ToString() : string.Empty);
    }

    private async Task<IntegrationResult> RecordAsync(
        string operation, object request, string key, bool success, string status, string message,
        int attempts, int durationMs, int? httpStatusCode, string errorCategory,
        CancellationToken cancellationToken, object? response = null, object? resultData = null)
    {
        var record = new IntegrationCallRecord
        {
            Provider = "WeCom",
            Operation = operation,
            Status = status,
            CorrelationId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = key,
            ErrorCategory = errorCategory,
            RequestJson = JsonSerializer.Serialize(request),
            ResponseJson = JsonSerializer.Serialize(response ?? new { message }),
            IsSuccess = success,
            AttemptCount = attempts,
            DurationMs = durationMs,
            HttpStatusCode = httpStatusCode
        };
        _context.IntegrationCallRecords.Add(record);
        await _context.SaveChangesAsync(cancellationToken);
        return new IntegrationResult(success, status, message, resultData ?? response);
    }

    private Task<IntegrationCallRecord?> FindSucceededAsync(string provider, string operation, string key, CancellationToken cancellationToken) =>
        _context.IntegrationCallRecords.AsNoTracking().FirstOrDefaultAsync(record => record.Provider == provider
            && record.Operation == operation && record.IdempotencyKey == key && record.IsSuccess, cancellationToken);

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken,
        Action<int> setAttempt)
    {
        HttpResponseMessage? lastResponse = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            setAttempt(attempt);
            lastResponse?.Dispose();
            try
            {
                lastResponse = await CreateClient().SendAsync(requestFactory(), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!IsTransient(lastResponse.StatusCode) || attempt == 3) return lastResponse;
            }
            catch (HttpRequestException) when (attempt < 3)
            {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(attempt * 200), cancellationToken);
        }
        return lastResponse ?? throw new HttpRequestException("企业微信请求失败");
    }

    private sealed record WeComApiResponse(int ErrorCode, string ErrorMessage, string AccessToken, string MessageId)
    {
        public int errcode => ErrorCode;
        public string errmsg => ErrorMessage;
        public string msgid => MessageId;
    }

    internal static bool IsTransient(HttpStatusCode statusCode) => statusCode == HttpStatusCode.RequestTimeout
        || (int)statusCode == 429 || (int)statusCode >= 500;

    internal static string NormalizeIdempotencyKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value.Trim()[..Math.Min(value.Trim().Length, 128)];

    internal static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string MaskIdentifier(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length <= 2) return "**";
        return $"{normalized[0]}***{normalized[^1]}";
    }

    internal static string SafeError(Exception exception)
    {
        var message = exception.Message.Replace("\r", " ").Replace("\n", " ");
        return message.Length <= 500 ? message : message[..500];
    }

    private static object ParseSafeJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new { error = value.Length <= 500 ? value : value[..500] };
        }
    }
}

public sealed class TencentMeetingGateway : ITencentMeetingGateway
{
    private readonly ApplicationDbContext _context;
    private readonly HttpClient _httpClient;
    private readonly ITencentMeetingLocalCliService _localMeeting;
    private readonly TencentMeetingOptions _options;

    public TencentMeetingGateway(
        ApplicationDbContext context,
        HttpClient httpClient,
        ITencentMeetingLocalCliService localMeeting,
        IOptions<TencentMeetingOptions> options)
    {
        _context = context;
        _httpClient = httpClient;
        _localMeeting = localMeeting;
        _options = options.Value;
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 3, 90));
    }

    public async Task<IntegrationResult> CreateMeetingAsync(
        string subject,
        DateTime startAt,
        DateTime endAt,
        CancellationToken cancellationToken = default,
        string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("会议主题不能为空", nameof(subject));
        if (endAt <= startAt) throw new ArgumentException("会议结束时间必须晚于开始时间", nameof(endAt));
        var audit = new { subjectLength = subject.Length, subjectSha256 = WeComGateway.Sha256(subject), startAt, endAt };
        var key = WeComGateway.NormalizeIdempotencyKey(idempotencyKey);
        var existing = await _context.IntegrationCallRecords.AsNoTracking().FirstOrDefaultAsync(record =>
            record.Provider == "TencentMeeting" && record.Operation == "CreateMeeting"
            && record.IdempotencyKey == key && record.IsSuccess, cancellationToken);
        if (existing != null) return new IntegrationResult(true, "Succeeded", "相同幂等请求已成功处理", new { existing.Id });
        if (!IsApiConfigured()) return await RecordAsync("CreateMeeting", audit, key, false, "NotConfigured", "腾讯会议 AK/SK 尚未完整配置", 0, 0, null, "Configuration", cancellationToken);

        var start = startAt.Kind == DateTimeKind.Utc ? startAt : startAt.ToUniversalTime();
        var end = endAt.Kind == DateTimeKind.Utc ? endAt : endAt.ToUniversalTime();
        var body = JsonSerializer.Serialize(new
        {
            userid = _options.DefaultUserId,
            instanceid = 1,
            subject = subject.Trim(),
            type = 0,
            start_time = new DateTimeOffset(start).ToUnixTimeSeconds().ToString(),
            end_time = new DateTimeOffset(end).ToUnixTimeSeconds().ToString()
        });
        const string uri = "/v1/meetings";
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        try
        {
            HttpResponseMessage? response = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                attempts = attempt;
                response?.Dispose();
                var request = BuildSignedRequest(HttpMethod.Post, uri, body);
                response = await CreateClient().SendAsync(request, cancellationToken);
                if (!WeComGateway.IsTransient(response.StatusCode) || attempt == 3) break;
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 200), cancellationToken);
            }
            if (response == null) throw new HttpRequestException("腾讯会议请求失败");
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            var safeResponse = SummarizeMeetingResponse(responseText);
            var success = response.IsSuccessStatusCode && !HasTencentError(responseText);
            return await RecordAsync("CreateMeeting", audit, key, success, success ? "Succeeded" : "Rejected",
                success ? "腾讯会议创建成功" : "腾讯会议拒绝创建请求", attempts,
                (int)stopwatch.ElapsedMilliseconds, (int)response.StatusCode,
                success ? string.Empty : "Provider", cancellationToken, safeResponse, safeResponse);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return await RecordAsync("CreateMeeting", audit, key, false, "Failed", "腾讯会议调用失败",
                Math.Max(attempts, 1), (int)stopwatch.ElapsedMilliseconds, null,
                ex is TaskCanceledException ? "Timeout" : "Transport", cancellationToken,
                new { error = WeComGateway.SafeError(ex) });
        }
    }

    public async Task<IntegrationResult> GetTranscriptAsync(
        string meetingId,
        CancellationToken cancellationToken = default,
        string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(meetingId)) throw new ArgumentException("录制文件 ID 不能为空", nameof(meetingId));
        var audit = new { recordFileId = WeComGateway.MaskIdentifier(meetingId) };
        var key = WeComGateway.NormalizeIdempotencyKey(idempotencyKey);
        if (!_options.Enabled) return await RecordAsync("GetTranscript", audit, key, false, "NotConfigured", "腾讯会议集成未启用", 0, 0, null, "Configuration", cancellationToken);
        if (!_options.UseLocalCliFetch)
            return await RecordAsync("GetTranscript", audit, key, false, "RequiresStsToken", "云端转写接口需要 STS-Token；请启用本地 CLI 或配置票据回调", 0, 0, null, "Configuration", cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transcript = await _localMeeting.FetchSingleMeetTranscriptAsync(meetingId.Trim());
            cancellationToken.ThrowIfCancellationRequested();
            var content = transcript.CleanText ?? string.Empty;
            var data = new { transcript.CleanText, transcript.RawJson, transcript.RecordId, transcript.Title };
            return await RecordAsync("GetTranscript", audit, key, true, "Succeeded", "腾讯会议转写获取成功", 1,
                (int)stopwatch.ElapsedMilliseconds, null, string.Empty, cancellationToken,
                new { characterCount = content.Length, contentSha256 = WeComGateway.Sha256(content) }, data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await RecordAsync("GetTranscript", audit, key, false, "Failed", "腾讯会议转写获取失败", 1,
                (int)stopwatch.ElapsedMilliseconds, null, "Provider", cancellationToken,
                new { error = WeComGateway.SafeError(ex) });
        }
    }

    internal HttpRequestMessage BuildSignedRequest(HttpMethod method, string uri, string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = RandomNumberGenerator.GetInt32(100000, int.MaxValue).ToString();
        var signature = Sign(_options.SecretId, _options.SecretKey, method.Method, nonce, timestamp, uri, body);
        var request = new HttpRequestMessage(method, $"{(_options.ApiBaseUrl ?? string.Empty).TrimEnd('/')}{uri}");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("X-TC-Key", _options.SecretId);
        request.Headers.TryAddWithoutValidation("X-TC-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-TC-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-TC-Signature", signature);
        request.Headers.TryAddWithoutValidation("AppId", _options.AppId);
        if (!string.IsNullOrWhiteSpace(_options.SdkId)) request.Headers.TryAddWithoutValidation("SdkId", _options.SdkId);
        request.Headers.TryAddWithoutValidation("X-TC-Registered", "1");
        return request;
    }

    internal static string Sign(string secretId, string secretKey, string method, string nonce, string timestamp, string uri, string body)
    {
        var source = $"{method}\nX-TC-Key={secretId}&X-TC-Nonce={nonce}&X-TC-Timestamp={timestamp}\n{uri}\n{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        var hex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(hex));
    }

    private bool IsApiConfigured() => _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.ApiBaseUrl)
        && !string.IsNullOrWhiteSpace(_options.AppId)
        && !string.IsNullOrWhiteSpace(_options.SecretId)
        && !string.IsNullOrWhiteSpace(_options.SecretKey)
        && !string.IsNullOrWhiteSpace(_options.DefaultUserId);

    private HttpClient CreateClient()
    {
        if (!_httpClient.DefaultRequestHeaders.Accept.Any(value => value.MediaType == "application/json"))
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return _httpClient;
    }

    private async Task<IntegrationResult> RecordAsync(
        string operation, object request, string key, bool success, string status, string message,
        int attempts, int durationMs, int? httpStatusCode, string errorCategory,
        CancellationToken cancellationToken, object? response = null, object? resultData = null)
    {
        _context.IntegrationCallRecords.Add(new IntegrationCallRecord
        {
            Provider = "TencentMeeting",
            Operation = operation,
            Status = status,
            CorrelationId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = key,
            ErrorCategory = errorCategory,
            RequestJson = JsonSerializer.Serialize(request),
            ResponseJson = JsonSerializer.Serialize(response ?? new { message }),
            IsSuccess = success,
            AttemptCount = attempts,
            DurationMs = durationMs,
            HttpStatusCode = httpStatusCode
        });
        await _context.SaveChangesAsync(cancellationToken);
        return new IntegrationResult(success, status, message, resultData ?? response);
    }

    private static bool HasTencentError(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = document.RootElement;
        return root.TryGetProperty("error_info", out _) || root.TryGetProperty("error_code", out _);
    }

    private static object SummarizeMeetingResponse(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = document.RootElement;
        if (root.TryGetProperty("meeting_info_list", out var meetings) && meetings.ValueKind == JsonValueKind.Array)
        {
            var first = meetings.EnumerateArray().FirstOrDefault();
            return new
            {
                meetingCount = root.TryGetProperty("meeting_number", out var count) ? count.ToString() : meetings.GetArrayLength().ToString(),
                meetingId = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("meeting_id", out var id) ? id.GetString() : null,
                startTime = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("start_time", out var start) ? start.GetString() : null,
                endTime = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("end_time", out var end) ? end.GetString() : null
            };
        }
        if (root.TryGetProperty("error_info", out var errorInfo)) return errorInfo.Clone();
        return root.Clone();
    }
}

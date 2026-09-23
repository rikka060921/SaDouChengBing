using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using ToDo.Domain.Dto;
using ToDo.Domain.Options;

namespace ToDo.Domain;

/// <summary>
/// 龙虾MCP云端接口实现，替代TencentMeetingLocalCliService(tmeet.cmd)
/// MCP网关地址：https://mcp.meeting.tencent.com/mcp/wemeet-open/v1
/// 请求头鉴权：X-Tencent-Meeting-Token
/// </summary>
public class TencentMeetingMcpService : ITencentMeetingLocalCliService
{
    private readonly HttpClient _httpClient;
    private readonly TencentMeetingOptions _meetingOptions;
    private readonly ILogger<TencentMeetingMcpService> _logger;
    private readonly string _mcpToken;
    private const string MCP_GATEWAY = "https://mcp.meeting.tencent.com/mcp/wemeet-open/v1";

    public TencentMeetingMcpService(
        HttpClient httpClient,
        IOptions<TencentMeetingOptions> meetingOptions,
        ILogger<TencentMeetingMcpService> logger)
    {
        _httpClient = httpClient;
        _meetingOptions = meetingOptions.Value;
        _logger = logger;
        _mcpToken = Environment.GetEnvironmentVariable("TENCENT_MEETING_TOKEN") ?? string.Empty;

        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        // 设置默认请求头
        _httpClient.DefaultRequestHeaders.Clear();
        if (!string.IsNullOrWhiteSpace(_mcpToken))
        {
            _httpClient.DefaultRequestHeaders.Add("X-Tencent-Meeting-Token", _mcpToken);
        }
        else
        {
            _logger.LogWarning("Tencent Meeting transcript integration is disabled because TENCENT_MEETING_TOKEN is not configured");
        }
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    #region 私有方法：通用MCP JSON-RPC 请求封装
    /// <summary>
    /// MCP标准JSON-RPC2.0调用
    /// </summary>
    private async Task<JsonElement> CallMcpToolAsync(string toolName, object arguments)
    {
        if (string.IsNullOrWhiteSpace(_mcpToken))
        {
            throw new InvalidOperationException("腾讯会议转写功能尚未配置，请联系管理员配置 TENCENT_MEETING_TOKEN");
        }

        var requestBody = new
        {
            jsonrpc = "2.0",
            id = Guid.NewGuid().ToString("N"),
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments = arguments
            }
        };

        var json = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { WriteIndented = false });
        var requestMsg = new HttpRequestMessage(HttpMethod.Post, MCP_GATEWAY)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // 添加Token到请求头
        if (!requestMsg.Headers.Contains("X-Tencent-Meeting-Token"))
        {
            requestMsg.Headers.Add("X-Tencent-Meeting-Token", _mcpToken);
        }

        try
        {
            _logger.LogDebug("Calling Tencent Meeting MCP tool {ToolName}", toolName);
            var response = await _httpClient.SendAsync(requestMsg);

            var respText = await response.Content.ReadAsStringAsync();
            _logger.LogDebug("Tencent Meeting MCP tool {ToolName} returned {StatusCode} with {ResponseLength} characters",
                toolName, (int)response.StatusCode, respText.Length);

            if (!response.IsSuccessStatusCode)
            {
                // 尝试解析错误信息
                string errorMsg = $"MCP服务返回异常状态码 {(int)response.StatusCode}";
                try
                {
                    using var errorDoc = JsonDocument.Parse(respText);
                    if (errorDoc.RootElement.TryGetProperty("error", out var errorEl))
                    {
                        if (errorEl.TryGetProperty("message", out var msgEl))
                        {
                            errorMsg += $"，错误信息：{msgEl.GetString()}";
                        }
                    }
                }
                catch { }

                throw new HttpRequestException(errorMsg);
            }

            // 使用不同的变量名解析成功响应
            using var successDoc = JsonDocument.Parse(respText);
            var root = successDoc.RootElement;

            // 判断是否返回错误
            if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null)
            {
                var msg = errorElement.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "MCP调用未知错误";
                var code = errorElement.TryGetProperty("code", out var codeEl) ? codeEl.GetInt32() : 0;
                _logger.LogWarning("Tencent Meeting MCP tool {ToolName} returned business error {ErrorCode}", toolName, code);
                throw new InvalidOperationException($"MCP接口调用失败：{msg} (code={code})");
            }

            // 获取result内容
            if (!root.TryGetProperty("result", out var resultEl))
            {
                _logger.LogWarning("Tencent Meeting MCP tool {ToolName} response is missing result", toolName);
                throw new InvalidOperationException("MCP接口返回缺少result节点");
            }

            // 检查result中是否有content字段（MCP标准响应格式）
            if (resultEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
            {
                if (contentEl.GetArrayLength() > 0)
                {
                    var firstContent = contentEl[0];
                    if (firstContent.TryGetProperty("text", out var textEl))
                    {
                        try
                        {
                            using var textDoc = JsonDocument.Parse(textEl.GetString() ?? "{}");
                            return textDoc.RootElement;
                        }
                        catch
                        {
                            // 如果不是JSON，返回原来的result
                        }
                    }
                }
            }

            return resultEl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tencent Meeting MCP tool {ToolName} call failed", toolName);
            throw;
        }
    }
    #endregion

    #region 查询当日已结束会议列表 【对应 tmeet meeting list-ended】
    public async Task<List<TencentMeetDailyItem>> QueryDailyMeetListAsync(DateTime meetDate)
    {
        var start = meetDate.Date.ToString("yyyy-MM-ddTHH:mm:ss+08:00");
        var end = meetDate.Date.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ss+08:00");

        // MCP工具名称：get_user_ended_meetings
        var args = new
        {
            start_time = start,
            end_time = end
        };
        var resultEl = await CallMcpToolAsync("get_user_ended_meetings", args);

        // 解析返回会议列表，映射到 TencentMeetDailyItem
        // 支持多种可能的返回格式
        var meetingsList = new List<JsonElement>();

        // 尝试直接获取meetings数组
        if (resultEl.TryGetProperty("meetings", out var meetingsEl) && meetingsEl.ValueKind == JsonValueKind.Array)
        {
            meetingsList.AddRange(meetingsEl.EnumerateArray());
        }
        // 尝试获取data.meetings
        else if (resultEl.TryGetProperty("data", out var dataEl) &&
                 dataEl.TryGetProperty("meetings", out var dataMeetingsEl) &&
                 dataMeetingsEl.ValueKind == JsonValueKind.Array)
        {
            meetingsList.AddRange(dataMeetingsEl.EnumerateArray());
        }
        // 如果直接就是数组
        else if (resultEl.ValueKind == JsonValueKind.Array)
        {
            meetingsList.AddRange(resultEl.EnumerateArray());
        }
        else
        {
            // 尝试从content中提取
            if (resultEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in contentEl.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var textEl))
                    {
                        try
                        {
                            using var textDoc = JsonDocument.Parse(textEl.GetString() ?? "{}");
                            var parsed = textDoc.RootElement;
                            if (parsed.TryGetProperty("meetings", out var parsedMeetings) &&
                                parsedMeetings.ValueKind == JsonValueKind.Array)
                            {
                                meetingsList.AddRange(parsedMeetings.EnumerateArray());
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        if (!meetingsList.Any())
        {
            _logger.LogDebug("Tencent Meeting MCP returned no meeting list data");
            return new List<TencentMeetDailyItem>();
        }

        var list = new List<TencentMeetDailyItem>();
        foreach (var item in meetingsList)
        {
            // 提取字段，支持不同的字段名
            var recordFileId = GetStringProperty(item, new[] { "record_file_id", "recordFileId", "id" });
            var subject = GetStringProperty(item, new[] { "subject", "meeting_subject", "title" });
            var meetingCode = GetStringProperty(item, new[] { "meeting_code", "meetingCode", "code" });
            var startTimeStr = GetStringProperty(item, new[] { "start_time", "startTime", "meeting_start_time" });

            var hasTranscript = GetBoolProperty(item, new[] { "has_transcript", "hasTranscript", "has_record" });
            var duration = GetIntProperty(item, new[] { "duration_minute", "durationMinute", "duration" });

            if (string.IsNullOrEmpty(recordFileId))
                continue;

            if (_meetingOptions.NeedTranscriptOnly && !hasTranscript)
                continue;

            DateTime.TryParse(startTimeStr, out var startDt);
            var shortTime = startDt.ToString("HH:mm");

            list.Add(new TencentMeetDailyItem
            {
                RecordFileId = recordFileId,
                Subject = subject ?? "未命名会议",
                MeetingCode = meetingCode ?? string.Empty,
                StartTimeShort = shortTime,
                DurationMinute = duration,
                HasTranscript = hasTranscript,
                StartTime = startDt
            });
        }

        return list.OrderBy(x => x.StartTime).ToList();
    }

    // 辅助方法：从JsonElement中获取字符串属性（支持多个可能的字段名）
    private string GetStringProperty(JsonElement element, string[] possibleNames)
    {
        foreach (var name in possibleNames)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private bool GetBoolProperty(JsonElement element, string[] possibleNames)
    {
        foreach (var name in possibleNames)
        {
            if (element.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
                    return prop.GetBoolean();
                if (prop.ValueKind == JsonValueKind.String)
                {
                    var str = prop.GetString();
                    return str == "true" || str == "1" || str == "yes";
                }
                if (prop.ValueKind == JsonValueKind.Number)
                    return prop.GetInt32() > 0;
            }
        }
        return false;
    }

    private int GetIntProperty(JsonElement element, string[] possibleNames)
    {
        foreach (var name in possibleNames)
        {
            if (element.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number)
                    return prop.GetInt32();
                if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var val))
                    return val;
            }
        }
        return 0;
    }
    #endregion

    #region 根据recordFileId拉取单场会议转写文本 【对应 tmeet record transcript-get】
    public async Task<TencentMeetTranscriptResult> FetchSingleMeetTranscriptAsync(string recordFileId, string? title = null)
    {
        var args = new { record_file_id = recordFileId };
        var resultEl = await CallMcpToolAsync("get_transcript", args);

        var rawJson = resultEl.ToString();
        string subject = title ?? "未知会议";

        // 尝试多种方式获取标题
        subject = GetStringProperty(resultEl, new[] { "subject", "meeting_subject", "title" }) ?? subject;

        // 清洗转写文本
        var cleanSb = new StringBuilder();
        var paragraphs = ExtractParagraphs(resultEl);

        foreach (var para in paragraphs)
        {
            var speaker = GetStringProperty(para, new[] { "speaker", "speaker_name", "user_name" });
            var text = GetStringProperty(para, new[] { "text", "content", "message" });

            if (!string.IsNullOrEmpty(speaker) && !string.IsNullOrEmpty(text))
            {
                cleanSb.AppendLine($"{speaker}：{text}");
            }
            else if (!string.IsNullOrEmpty(text))
            {
                cleanSb.AppendLine(text);
            }
        }

        return new TencentMeetTranscriptResult
        {
            RawJson = rawJson,
            CleanText = cleanSb.ToString().Trim(),
            Title = subject,
            RecordId = recordFileId
        };
    }

    // 提取段落列表
    private List<JsonElement> ExtractParagraphs(JsonElement resultEl)
    {
        var paragraphs = new List<JsonElement>();

        // 尝试直接获取paragraphs
        if (resultEl.TryGetProperty("paragraphs", out var pEl) && pEl.ValueKind == JsonValueKind.Array)
        {
            paragraphs.AddRange(pEl.EnumerateArray());
        }
        // 尝试从data中获取
        else if (resultEl.TryGetProperty("data", out var dataEl) &&
                 dataEl.TryGetProperty("paragraphs", out var dataPEl) &&
                 dataPEl.ValueKind == JsonValueKind.Array)
        {
            paragraphs.AddRange(dataPEl.EnumerateArray());
        }
        // 尝试从content中获取
        else if (resultEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in contentEl.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var textEl))
                {
                    try
                    {
                        using var textDoc = JsonDocument.Parse(textEl.GetString() ?? "{}");
                        var parsed = textDoc.RootElement;
                        if (parsed.TryGetProperty("paragraphs", out var parsedP) &&
                            parsedP.ValueKind == JsonValueKind.Array)
                        {
                            paragraphs.AddRange(parsedP.EnumerateArray());
                        }
                    }
                    catch { }
                }
            }
        }
        // 尝试获取utterances（另一种可能的字段名）
        else if (resultEl.TryGetProperty("utterances", out var uEl) && uEl.ValueKind == JsonValueKind.Array)
        {
            paragraphs.AddRange(uEl.EnumerateArray());
        }

        return paragraphs;
    }
    #endregion

    #region 批量合并多场会议
    public async Task<(string MergedCleanContent, string AllRawJson, string RecordIdsJoin)> BatchFetchAndMergeAsync(
        List<string> recordFileIds,
        Dictionary<string, string>? titleMap = null)
    {
        if (recordFileIds == null || !recordFileIds.Any())
            throw new ArgumentException("未勾选任何会议", nameof(recordFileIds));

        var validIds = recordFileIds.Where(id => !string.IsNullOrEmpty(id)).ToList();
        if (!validIds.Any())
            throw new ArgumentException("勾选的会议都没有转写文件，请选择有转写的会议");

        var sbMerge = new StringBuilder();
        var allRawList = new List<string>();
        var recordJoin = string.Join(",", validIds.Distinct());

        foreach (var rid in validIds.Distinct())
        {
            try
            {
                string title = titleMap?.GetValueOrDefault(rid) ?? rid;
                var one = await FetchSingleMeetTranscriptAsync(rid, title);
                allRawList.Add(one.RawJson);

                // ✅ 使用 Markdown 二级标题格式（预览区会显示为带颜色的标题）
                sbMerge.AppendLine($"## 📝 {one.Title}");
                sbMerge.AppendLine();
                sbMerge.AppendLine(one.CleanText);
                sbMerge.AppendLine();
                sbMerge.AppendLine("---");
                sbMerge.AppendLine();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Tencent Meeting transcript for record {RecordId}", rid);
                sbMerge.AppendLine($"## ❌ 会议 {rid}");
                sbMerge.AppendLine();
                sbMerge.AppendLine($"拉取失败：{ex.Message}");
                sbMerge.AppendLine();
                sbMerge.AppendLine("---");
                sbMerge.AppendLine();
            }
        }

        var rawJoin = string.Join("|||", allRawList);
        return (sbMerge.ToString().TrimEnd(), rawJoin, recordJoin);
    }
    #endregion
}

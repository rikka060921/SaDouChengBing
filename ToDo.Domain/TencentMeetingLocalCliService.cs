using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using ToDo.Domain.Dto;
using ToDo.Domain.Options;

namespace ToDo.Domain;

public class TencentMeetingLocalCliService : ITencentMeetingLocalCliService
{
    private readonly MeetingCliOptions _options;
    private readonly string _cacheDir;
    private readonly ILogger<TencentMeetingLocalCliService> _logger;

    public TencentMeetingLocalCliService(
        IOptions<MeetingCliOptions> options,
        ILogger<TencentMeetingLocalCliService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _cacheDir = Path.Combine(Directory.GetCurrentDirectory(), _options.MeetingCacheRoot);
        Directory.CreateDirectory(_cacheDir);
    }

    #region 安全执行CLI（支持降级不抛异常）
    internal virtual async Task<string> ExecuteCliCommandAsync(List<string> args, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.CliPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.EnvironmentVariables["PYTHONUTF8"] = "1";
        psi.EnvironmentVariables["CHCP"] = "65001";

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, _options.CliTimeoutSeconds)));
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    errorBuilder.AppendLine(e.Data);
                }
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0)
            {
                var err = errorBuilder.ToString();
                if (throwOnError)
                {
                    throw new InvalidOperationException($"tmeet命令执行失败，退出码：{process.ExitCode}，错误信息：{err}");
                }
                return string.Empty;
            }

            var rawOutput = outputBuilder.ToString();

            var rawLogPath = Path.GetFullPath("tmeet_raw_output.log");
            await File.WriteAllTextAsync(rawLogPath, rawOutput, Encoding.UTF8);

            var cleanOutput = ExtractPureJson(rawOutput);
            var cleanLogPath = Path.GetFullPath("tmeet_clean_json.log");
            await File.WriteAllTextAsync(cleanLogPath, cleanOutput, Encoding.UTF8);

            return cleanOutput;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            if (throwOnError)
            {
                throw new TimeoutException($"tmeet命令执行超时（{_options.CliTimeoutSeconds}秒）");
            }
            return string.Empty;
        }
        catch (Exception ex)
        {
            if (throwOnError)
            {
                throw new InvalidOperationException($"tmeet命令执行异常：{ex.Message}", ex);
            }
            return string.Empty;
        }
    }

    private string ExtractPureJson(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return string.Empty;

        var startIndex = rawText.IndexOf('{');
        var endIndex = rawText.LastIndexOf('}');
        if (startIndex >= 0 && endIndex > startIndex)
        {
            return rawText.Substring(startIndex, endIndex - startIndex + 1);
        }
        startIndex = rawText.IndexOf('[');
        endIndex = rawText.LastIndexOf(']');
        if (startIndex >= 0 && endIndex > startIndex)
        {
            return rawText.Substring(startIndex, endIndex - startIndex + 1);
        }
        return rawText;
    }
    #endregion

    #region 检查认证状态
    private async Task<bool> IsCliLoggedInAsync()
    {
        // 与其他命令共用超时和 stderr 读取，登录后再次刷新立即生效。
        var output = await ExecuteCliCommandAsync(["auth", "status"], false);
        return output.Contains("Logged in", StringComparison.OrdinalIgnoreCase);
    }
    #endregion

    #region 缓存辅助方法
    private async Task<string> GetCachedRecordFileIdAsync(string meetingId)
    {
        try
        {
            var cacheFile = Path.Combine(_cacheDir, "record_id_cache.json");
            if (!File.Exists(cacheFile))
                return "";

            var json = await File.ReadAllTextAsync(cacheFile);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(meetingId, out var idEl))
            {
                return idEl.GetString() ?? "";
            }
            return "";
        }
        catch
        {
            return "";
        }
    }

    private async Task CacheRecordFileIdAsync(string meetingId, string recordFileId)
    {
        try
        {
            var cacheFile = Path.Combine(_cacheDir, "record_id_cache.json");
            Dictionary<string, string> cache;
            if (File.Exists(cacheFile))
            {
                var json = await File.ReadAllTextAsync(cacheFile);
                cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
            }
            else
            {
                cache = new Dictionary<string, string>();
            }
            cache[meetingId] = recordFileId;
            await WriteCacheAsync(cacheFile, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache record file ID for meeting {MeetingId}", meetingId);
        }
    }

    private async Task<List<TencentMeetDailyItem>?> GetCachedDailyListAsync(DateTime meetDate)
    {
        try
        {
            var dateStr = meetDate.ToString("yyyy-MM-dd");
            var cacheFile = Path.Combine(_cacheDir, $"daily_list_{dateStr}.json");
            if (!File.Exists(cacheFile))
                return null;

            var json = await File.ReadAllTextAsync(cacheFile);
            return JsonSerializer.Deserialize<List<TencentMeetDailyItem>>(json);
        }
        catch
        {
            return null;
        }
    }

    private async Task CacheDailyListAsync(DateTime meetDate, List<TencentMeetDailyItem> list)
    {
        try
        {
            var dateStr = meetDate.ToString("yyyy-MM-dd");
            var cacheFile = Path.Combine(_cacheDir, $"daily_list_{dateStr}.json");
            await WriteCacheAsync(cacheFile, JsonSerializer.Serialize(list));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache daily list for {Date}", meetDate);
        }
    }

    private async Task<TencentMeetTranscriptResult?> GetCachedTranscriptAsync(string recordFileId)
    {
        try
        {
            var cacheFile = Path.Combine(_cacheDir, $"transcript_{recordFileId}.json");
            if (!File.Exists(cacheFile))
                return null;

            var json = await File.ReadAllTextAsync(cacheFile);
            return JsonSerializer.Deserialize<TencentMeetTranscriptResult>(json);
        }
        catch
        {
            return null;
        }
    }

    private async Task CacheTranscriptAsync(string recordFileId, TencentMeetTranscriptResult result)
    {
        try
        {
            var cacheFile = Path.Combine(_cacheDir, $"transcript_{recordFileId}.json");
            await WriteCacheAsync(cacheFile, JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache transcript for {RecordFileId}", recordFileId);
        }
    }
    private static async Task WriteCacheAsync(string path, string json)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
    #endregion

    #region 获取录制文件ID（支持降级到本地缓存）
    private async Task<string> GetRecordFileIdAsync(string meetingId)
    {
        // 先检查是否有本地缓存
        var cachedId = await GetCachedRecordFileIdAsync(meetingId);
        if (!string.IsNullOrEmpty(cachedId))
            return cachedId;

        // 检查CLI是否已登录
        if (!await IsCliLoggedInAsync())
        {
            _logger.LogWarning("tmeet 未登录，且无本地缓存，跳过获取录制文件ID");
            return "";
        }

        try
        {
            var start = DateTime.Now.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ss+08:00");
            var end = DateTime.Now.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ss+08:00");

            var args = new List<string>
            {
                "record", "list",
                "--meeting-id", meetingId,
                "--start", start,
                "--end", end,
                "--format", "json"
            };

            var output = await ExecuteCliCommandAsync(args, false);
            if (string.IsNullOrWhiteSpace(output))
                return "";

            using var doc = JsonDocument.Parse(output);

            if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                dataEl.TryGetProperty("record_meetings", out var recordMeetings) &&
                recordMeetings.ValueKind == JsonValueKind.Array &&
                recordMeetings.GetArrayLength() > 0)
            {
                var firstMeeting = recordMeetings[0];
                if (firstMeeting.TryGetProperty("record_files", out var recordFiles) &&
                    recordFiles.ValueKind == JsonValueKind.Array &&
                    recordFiles.GetArrayLength() > 0)
                {
                    var firstFile = recordFiles[0];
                    if (firstFile.TryGetProperty("record_file_id", out var idEl))
                    {
                        var recordFileId = idEl.GetString() ?? "";
                        if (!string.IsNullOrEmpty(recordFileId))
                        {
                            await CacheRecordFileIdAsync(meetingId, recordFileId);
                        }
                        return recordFileId;
                    }
                }
            }
            return "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve Tencent Meeting record for meeting {MeetingId}", meetingId);
            return "";
        }
    }
    #endregion

    #region 查询当日会议列表（支持降级到本地缓存）
    public async Task<List<TencentMeetDailyItem>> QueryDailyMeetListAsync(DateTime meetDate)
    {
        // 每次刷新优先查询最新数据，缓存仅在离线或请求失败时兜底。
        var cachedList = await GetCachedDailyListAsync(meetDate);

        // 检查CLI是否已登录
        if (!await IsCliLoggedInAsync())
        {
            _logger.LogWarning("tmeet 未登录，使用会议列表缓存（如有）");
            return cachedList ?? new List<TencentMeetDailyItem>();
        }

        var dateStart = meetDate.Date.ToString("yyyy-MM-ddTHH:mm:ss+08:00");
        var dateEnd = meetDate.Date.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ss+08:00");

        var args = new List<string>
        {
            "meeting", "list-ended",
            "--start", dateStart,
            "--end", dateEnd,
            "--format", "json"
        };

        var jsonOutput = await ExecuteCliCommandAsync(args, false);
        if (string.IsNullOrWhiteSpace(jsonOutput))
            return cachedList ?? new List<TencentMeetDailyItem>();

        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var responseData)
                || responseData.ValueKind != JsonValueKind.Object
                || !responseData.TryGetProperty("meeting_info_list", out var responseMeetings)
                || responseMeetings.ValueKind != JsonValueKind.Array)
                return cachedList ?? new List<TencentMeetDailyItem>();
            var list = new List<TencentMeetDailyItem>();

            if (doc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
            {
                if (dataEl.TryGetProperty("meeting_info_list", out var meetingsEl) && meetingsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in meetingsEl.EnumerateArray())
                    {
                        var meetingId = item.TryGetProperty("meeting_id", out var idEl) ? idEl.GetString() : "";
                        var subject = item.TryGetProperty("subject", out var subjectEl) ? subjectEl.GetString() ?? "未命名会议" : "未命名会议";
                        var meetingCode = item.TryGetProperty("meeting_code", out var codeEl) ? codeEl.GetString() ?? string.Empty : string.Empty;
                        var startTimeStr = item.TryGetProperty("start_time", out var startEl) ? startEl.GetString() : "";
                        var endTimeStr = item.TryGetProperty("end_time", out var endEl) ? endEl.GetString() : "";

                        if (string.IsNullOrEmpty(meetingId))
                            continue;

                        var recordFileId = await GetRecordFileIdAsync(meetingId);

                        DateTime.TryParse(startTimeStr, out var startDt);
                        DateTime.TryParse(endTimeStr, out var endDt);
                        var duration = (int)(endDt - startDt).TotalMinutes;
                        if (duration < 0) duration = 0;

                        list.Add(new TencentMeetDailyItem
                        {
                            RecordFileId = recordFileId,
                            Subject = subject,
                            MeetingCode = meetingCode,
                            StartTimeShort = startDt.ToString("HH:mm"),
                            DurationMinute = duration,
                            HasTranscript = !string.IsNullOrEmpty(recordFileId),
                            StartTime = startDt
                        });
                    }
                }
            }

            var result = list.Where(x => !string.IsNullOrEmpty(x.RecordFileId)).OrderBy(x => x.StartTime).ToList();

            // 缓存到本地
            await CacheDailyListAsync(meetDate, result);

            _logger.LogDebug("Parsed {MeetingCount} Tencent meetings; {RecordCount} have recordings", list.Count, result.Count);
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to parse Tencent Meeting CLI list output ({OutputLength} characters)", jsonOutput.Length);
            return cachedList ?? new List<TencentMeetDailyItem>();
        }
    }
    #endregion

    #region 单场拉取转写并清洗文本（支持降级到本地缓存）
    public async Task<TencentMeetTranscriptResult> FetchSingleMeetTranscriptAsync(string recordFileId, string? title = null)
    {
        if (string.IsNullOrEmpty(recordFileId))
            throw new ArgumentException("recordFileId 不能为空", nameof(recordFileId));

        // 转写可能仍在生成或被修订；在线优先，失败时才使用已有缓存。
        var cachedResult = await GetCachedTranscriptAsync(recordFileId);

        // 检查CLI是否已登录
        if (!await IsCliLoggedInAsync())
        {
            if (cachedResult != null) return cachedResult;
            _logger.LogWarning("tmeet 未登录，且无本地缓存，无法获取转写内容: {RecordFileId}", recordFileId);
            throw new InvalidOperationException($"tmeet 未登录，且本地无缓存: {recordFileId}");
        }

        var args = new List<string>
        {
            "record", "transcript-get",
            "--record-file-id", recordFileId,
            "--format", "json"
        };
        var rawJson = await ExecuteCliCommandAsync(args, false);
        if (string.IsNullOrEmpty(rawJson))
        {
            if (cachedResult != null) return cachedResult;
            throw new InvalidOperationException($"获取转写内容失败: {recordFileId}");
        }

        var cleanText = CleanTranscriptRawText(rawJson);
        if (string.IsNullOrWhiteSpace(cleanText) || cleanText.StartsWith("解析失败:", StringComparison.Ordinal))
        {
            if (cachedResult != null) return cachedResult;
            throw new InvalidOperationException("会议转写尚未生成或返回格式无效，请稍后刷新");
        }

        // 只有通过结构和内容检查的响应才写入原始转写缓存。
        var cacheFile = Path.Combine(_cacheDir, $"{recordFileId}_{DateTime.Now:yyyyMMddHHmmss}.json");
        await File.WriteAllTextAsync(cacheFile, rawJson);

        var finalTitle = !string.IsNullOrEmpty(title) ? title : recordFileId;

        var result = new TencentMeetTranscriptResult
        {
            RawJson = rawJson,
            CleanText = cleanText,
            Title = finalTitle,
            RecordId = recordFileId
        };

        // 缓存清洗后的结果
        await CacheTranscriptAsync(recordFileId, result);

        return result;
    }

    private string CleanTranscriptRawText(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var sb = new StringBuilder();
            var lastSpeaker = "";

            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("data", out var dataEl) &&
                dataEl.ValueKind == JsonValueKind.Object &&
                dataEl.TryGetProperty("minutes", out var minutes) &&
                minutes.ValueKind == JsonValueKind.Object &&
                minutes.TryGetProperty("paragraphs", out var paragraphs) &&
                paragraphs.ValueKind == JsonValueKind.Array)
            {
                foreach (var para in paragraphs.EnumerateArray())
                {
                    string speaker = "未知";
                    if (para.TryGetProperty("speaker", out var sp) &&
                        sp.TryGetProperty("user_name", out var nameEl))
                    {
                        speaker = nameEl.GetString() ?? "未知";
                    }

                    if (para.TryGetProperty("sentences", out var sentences) &&
                        sentences.ValueKind == JsonValueKind.Array)
                    {
                        var textBuilder = new StringBuilder();
                        foreach (var sent in sentences.EnumerateArray())
                        {
                            if (sent.TryGetProperty("words", out var words) &&
                                words.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var word in words.EnumerateArray())
                                {
                                    if (word.TryGetProperty("text", out var textEl))
                                    {
                                        textBuilder.Append(textEl.GetString());
                                    }
                                }
                            }
                        }
                        var text = textBuilder.ToString().Trim();
                        if (!string.IsNullOrEmpty(text))
                        {
                            // 发言人切换时加空行
                            if (lastSpeaker != speaker && lastSpeaker != "")
                            {
                                sb.AppendLine();
                            }
                            sb.AppendLine($"{speaker}：{text}");
                            lastSpeaker = speaker;
                        }
                    }
                }
            }

            var result = sb.ToString();
            result = Regex.Replace(result, @"\[\d{2}:\d{2}:\d{2}\.\d{3}\]", "");
            result = Regex.Replace(result, @"\[\d{2}:\d{2}:\d{2}\]", "");
            result = Regex.Replace(result, @"\n{3,}", "\n\n");
            return result.Trim();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to parse Tencent Meeting transcript output ({OutputLength} characters)", rawJson.Length);
            return $"解析失败: {ex.Message}";
        }
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

                // Markdown 标题格式
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

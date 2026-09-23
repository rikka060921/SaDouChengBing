using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ToDo.Domain.Options;

public class TencentMeetingOptions
{
    public bool Enabled { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string SdkId { get; set; } = string.Empty;
    public string SecretId { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string DefaultUserId { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public int TokenExpireMinutes { get; set; }
    /// <summary>
    /// true 使用本地tmeet.cmd CLI；false 使用龙虾MCP云端接口
    /// </summary>
    public bool UseLocalCliFetch { get; set; }
    public bool FetchFinishedOnly { get; set; }
    public bool NeedTranscriptOnly { get; set; }
    public int TimeoutSeconds { get; set; } = 20;
}

namespace ToDo.Domain.Options;

public class MeetingCliOptions
{
    /// <summary>
    /// tmeet 命令行可执行文件名称
    /// </summary>
    public string CliPath { get; set; } = "tmeet";

    /// <summary>
    /// 命令执行超时秒数
    /// </summary>
    public int CliTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// 腾讯会议返回JSON缓存根目录
    /// </summary>
    public string MeetingCacheRoot { get; set; } = "App_Data/TencentMeetCache";

    /// <summary>
    /// 是否开启多场会议内容自动合并
    /// </summary>
    public bool EnableMergeMultiMeeting { get; set; } = true;
}

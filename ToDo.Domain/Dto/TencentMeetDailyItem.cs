namespace ToDo.Domain.Dto;

/// <summary>
/// 按日期查询到的单场腾讯会议简要信息（前端列表渲染）
/// </summary>
public class TencentMeetDailyItem
{
    /// <summary>
    /// 录制文件唯一ID，用于拉取转写内容
    /// </summary>
    public string RecordFileId { get; set; } = string.Empty;

    /// <summary>
    /// 会议主题
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// 会议号
    /// </summary>
    public string MeetingCode { get; set; } = string.Empty;

    /// <summary>
    /// 开始时间 HH:mm
    /// </summary>
    public string StartTimeShort { get; set; } = string.Empty;

    /// <summary>
    /// 会议时长（分钟）
    /// </summary>
    public int DurationMinute { get; set; }

    /// <summary>
    /// 是否存在可用转写文稿
    /// </summary>
    public bool HasTranscript { get; set; } = true;

    /// <summary>
    /// 完整开始时间（用于后端排序）
    /// </summary>
    public DateTime StartTime { get; set; }
}

/// <summary>
/// 单场会议拉取返回完整转写结果
/// </summary>
public class TencentMeetTranscriptResult
{
    /// <summary>
    /// 原始CLI返回完整JSON
    /// </summary>
    public string RawJson { get; set; } = string.Empty;

    /// <summary>
    /// 清洗后纯文本内容
    /// </summary>
    public string CleanText { get; set; } = string.Empty;

    /// <summary>
    /// 会议标题
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// RecordFileId
    /// </summary>
    public string RecordId { get; set; } = string.Empty;
}

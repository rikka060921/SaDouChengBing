namespace ToDo.Domain.Options;

/// <summary>数据留存策略。默认不自动执行，管理员可先在治理页面预览。</summary>
public sealed class DataRetentionOptions
{
    public bool Enabled { get; set; }
    public int ArchiveAiSessionsAfterDays { get; set; } = 180;
    public int DeleteReadNotificationsAfterDays { get; set; } = 90;
    public int BatchSize { get; set; } = 500;
    public int RunIntervalHours { get; set; } = 24;
}

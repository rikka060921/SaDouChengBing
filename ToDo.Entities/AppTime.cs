namespace ToDo.Entities;

/// <summary>
/// 应用统一时间规则：业务界面和本地业务日期使用北京时间，跨时区持久化字段使用 UTC。
/// </summary>
public static class AppTime
{
    private static readonly TimeZoneInfo BeijingTimeZone = ResolveBeijingTimeZone();

    public static DateTime UtcNow => DateTime.UtcNow;

    /// <summary>不依赖服务器操作系统时区的北京时间。</summary>
    public static DateTime Now => ToBeijingTime(DateTime.UtcNow);

    /// <summary>不依赖服务器操作系统时区的北京自然日。</summary>
    public static DateTime Today => Now.Date;

    /// <summary>把数据库中按 UTC 保存的时间转换为北京时间。</summary>
    public static DateTime ToBeijingTime(DateTime utcTime)
    {
        var normalizedUtc = utcTime.Kind == DateTimeKind.Utc
            ? utcTime
            : DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(normalizedUtc, BeijingTimeZone);
    }

    /// <summary>把北京时间墙上时间转换为 UTC。</summary>
    public static DateTime ToUtc(DateTime beijingTime)
    {
        if (beijingTime.Kind == DateTimeKind.Utc) return beijingTime;
        var unspecified = DateTime.SpecifyKind(beijingTime, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, BeijingTimeZone);
    }

    private static TimeZoneInfo ResolveBeijingTimeZone()
    {
        foreach (var id in new[] { "China Standard Time", "Asia/Shanghai" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new InvalidOperationException("系统中没有可用的北京时间时区（China Standard Time / Asia/Shanghai）");
    }
}

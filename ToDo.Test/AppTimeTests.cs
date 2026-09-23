using ToDo.Entities;

namespace ToDo.Test;

public sealed class AppTimeTests
{
    [Fact]
    public void ToBeijingTime_AddsEightHoursAcrossDateBoundary()
    {
        var utc = new DateTime(2026, 8, 20, 16, 30, 0, DateTimeKind.Utc);

        var result = AppTime.ToBeijingTime(utc);

        Assert.Equal(new DateTime(2026, 8, 21, 0, 30, 0), result);
        Assert.Equal(DateTimeKind.Unspecified, result.Kind);
    }

    [Fact]
    public void ToBeijingTime_TreatsMySqlUnspecifiedValueAsUtc()
    {
        var mysqlValue = new DateTime(2026, 8, 20, 15, 10, 0, DateTimeKind.Unspecified);

        var result = AppTime.ToBeijingTime(mysqlValue);

        Assert.Equal(new DateTime(2026, 8, 20, 23, 10, 0), result);
    }

    [Fact]
    public void ToUtc_ConvertsBeijingWallClockWithoutUsingServerTimeZone()
    {
        var beijing = new DateTime(2026, 8, 20, 23, 10, 0, DateTimeKind.Unspecified);

        var result = AppTime.ToUtc(beijing);

        Assert.Equal(new DateTime(2026, 8, 20, 15, 10, 0, DateTimeKind.Utc), result);
    }
}

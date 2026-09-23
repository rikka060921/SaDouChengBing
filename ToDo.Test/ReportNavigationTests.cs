using ToDo.Razor.Pages.Reports;

namespace ToDo.Test;

public class ReportNavigationTests
{
    [Theory]
    [InlineData("project", null, null)]
    [InlineData("project", 1, 1)]
    [InlineData("project", 2, 2)]
    [InlineData("project", 3, 3)]
    [InlineData("project", 4, null)]
    [InlineData("project", 0, null)]
    [InlineData("project", 99, null)]
    [InlineData("team", null, 4)]
    [InlineData("team", 1, 4)]
    [InlineData("TEAM", 2, 4)]
    [InlineData(null, 3, 3)]
    public void ReportViews_KeepMemberSummarySeparateFromProjectTypes(string? tab, int? selected, int? expected)
        => Assert.Equal(expected, IndexModel.ResolveReportType(tab, selected));
}

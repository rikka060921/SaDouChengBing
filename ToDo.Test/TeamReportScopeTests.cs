using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;
using ToDo.Entities.DailySummary;

namespace ToDo.Test;

public class TeamReportScopeTests
{
    [Fact]
    public async Task GenerateTeamReportAsync_OnlyProcessesRequestedProject()
    {
        await using var context = CreateContext();
        context.Users.AddRange(
            new ApplicationUser { Id = 1, UserName = "leader-one", Status = UserStatus.Active },
            new ApplicationUser { Id = 2, UserName = "leader-two", Status = UserStatus.Active });
        context.Project.AddRange(
            NewProject(10, "Project One", 1),
            NewProject(20, "Project Two", 2));
        context.ProjectUsers.AddRange(
            new ProjectUser { ProjectId = 10, UserId = 1 },
            new ProjectUser { ProjectId = 20, UserId = 2 });
        var summaryOne = NewSummary(1, 1);
        var summaryTwo = NewSummary(2, 2);
        context.DailyWorkSummaries.AddRange(summaryOne, summaryTwo);
        await context.SaveChangesAsync();
        context.DailyProjectSummaryDetails.AddRange(
            NewDetail(summaryOne.Id, 10, "Project One"),
            NewDetail(summaryTwo.Id, 20, "Project Two"));
        await context.SaveChangesAsync();

        var ai = new RecordingAIService();
        var service = new PersonalDailySummaryService(
            context,
            ai,
            null!,
            null!,
            NullLogger<PersonalDailySummaryService>.Instance);

        var result = await service.GenerateTeamReportAsync(10, AppTime.Today);

        Assert.Equal(1, result.TotalProjects);
        Assert.Equal(1, result.GeneratedProjects);
        Assert.Equal([10], ai.ProjectIds);
        Assert.DoesNotContain(context.DailyReport, report => report.ProjectId == 20);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options);
    }

    private static Project NewProject(int id, string name, int leaderId) => new()
    {
        Id = id,
        Name = name,
        CreatedByUserId = leaderId,
        LeaderUserId = leaderId,
        Status = ProjectStatus.Active
    };

    private static DailyWorkSummary NewSummary(int id, int userId) => new()
    {
        Id = id,
        UserId = userId,
        SummaryDate = AppTime.Today,
        Title = $"Summary {id}",
        TotalSummary = "Completed work",
        SummaryStatus = 1
    };

    private static DailyProjectSummaryDetail NewDetail(int summaryId, int projectId, string projectName) => new()
    {
        DailySummaryId = summaryId,
        ProjectId = projectId,
        ProjectName = projectName,
        ProjectSummary = "Project progress"
    };

    private sealed class RecordingAIService : IAIService
    {
        public List<int> ProjectIds { get; } = [];

        public Task<AIDailyReportResult> GenerateTeamReportAsync(TeamReportInput input)
        {
            ProjectIds.Add(input.ProjectId);
            return Task.FromResult(new AIDailyReportResult
            {
                Success = true,
                GeneratedReport = $"Team report for {input.ProjectName}"
            });
        }

        public Task<string> GetChatCompletionAsync(string prompt) => throw new NotSupportedException();
        public Task<string> GetChatCompletionAsync(string prompt, AIChatOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AICompletionResult> GetChatCompletionWithUsageAsync(string prompt, AIChatOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AIMeetingMinutesResult> GenerateMeetingMinutesAsync(string transcript, string template, int projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AITaskSplitResult> SplitTaskAsync(string taskTitle, string taskDescription, string? expectedSubTaskCount = null) => throw new NotSupportedException();
        public Task<AIMeetingSummaryResult> ProcessMeetingMinutesAsync(string meetingContent) => throw new NotSupportedException();
        public Task<AIMeetingFullParseResult> ProcessMeetingMinutesFullStructAsync(string meetingContent, List<string>? memberNames = null, List<string>? projectNames = null) => throw new NotSupportedException();
        public Task<AIDailyReportResult> GenerateDailyReportAsync(AIDailyReportInput input) => throw new NotSupportedException();
        public Task<AIDailyReportResult> GenerateDailyReportByProjectAsync(DailyReportByProjectInput input) => throw new NotSupportedException();
        public Task<AITaskParseResult> ParseTaskTextAsync(string text, string? expectedCount = null) => throw new NotSupportedException();
    }
}

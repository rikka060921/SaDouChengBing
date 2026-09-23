using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Entities;
using ToDo.Razor.Pages.Tasks;

namespace ToDo.Test;

public sealed class TaskAssistancePageTests
{
    [Theory]
    [InlineData(AgentRunJobStatus.Pending, true)]
    [InlineData(AgentRunJobStatus.Running, true)]
    [InlineData(AgentRunJobStatus.Retrying, true)]
    [InlineData(AgentRunJobStatus.WaitingApproval, true)]
    [InlineData(AgentRunJobStatus.Completed, false)]
    [InlineData(AgentRunJobStatus.Failed, false)]
    [InlineData(AgentRunJobStatus.Cancelled, false)]
    public void OnlyActiveJobsPreventAnotherSubmission(AgentRunJobStatus status, bool active)
        => Assert.Equal(active, DetailsModel.IsAssistanceActive(status));

    [Fact]
    public void CompletedAdviceDoesNotClaimTaskCompletion()
    {
        Assert.Equal("建议已准备好", DetailsModel.GetAssistanceStatusLabel(AgentRunJobStatus.Completed));
        Assert.DoesNotContain("任务已完成", DetailsModel.GetAssistanceStatusLabel(AgentRunJobStatus.Completed));
    }

    [Fact]
    public void FailedJobDoesNotExposeRawProviderError()
    {
        var error = DetailsModel.GetAssistanceError(new AgentRunJob
        {
            Status = AgentRunJobStatus.Failed, ErrorMessage = "secret-provider-token and internal endpoint"
        });
        Assert.DoesNotContain("secret", error);
        Assert.Contains("重新请求", error);
        Assert.Empty(DetailsModel.GetAssistanceError(null));
    }

    [Fact]
    public void TaskSessionLinksRequireExplicitCurrentAccess()
    {
        var page = new DetailsModel(null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<DetailsModel>.Instance, null!);
        Assert.False(page.CanViewSession(null));
        Assert.False(page.CanViewSession(3));
        page.AccessibleSessionIds.Add(3);
        Assert.True(page.CanViewSession(3));
        Assert.False(page.CanViewSession(4));
    }
}

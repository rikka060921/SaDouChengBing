using ToDo.Entities;
using TaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Test;

public class TaskAssignmentInvariantTests
{
    [Fact]
    public void DigitalEmployeeTask_CannotBeClaimedByHuman()
    {
        var task = CreateTask();
        task.AssigneeType = TaskAssigneeType.DigitalEmployee;
        task.AgentDefinitionId = 7;

        Assert.False(task.CanBeClaimedByHuman);
    }

    [Theory]
    [InlineData(TaskStatus.Completed)]
    [InlineData(TaskStatus.Cancelled)]
    [InlineData(TaskStatus.PendingConfirmation)]
    public void ClosedOrReviewTask_CannotBeClaimedByHuman(TaskStatus status)
    {
        var task = CreateTask();
        task.Status = status;

        Assert.False(task.CanBeClaimedByHuman);
    }

    [Fact]
    public void UnassignedOpenHumanTask_CanBeClaimed()
    {
        var task = CreateTask();

        Assert.True(task.CanBeClaimedByHuman);
    }

    [Fact]
    public void ApprovingDigitalEmployeeResult_CompletesTaskAndAgentTogether()
    {
        var task = CreateTask();
        task.AssigneeType = TaskAssigneeType.DigitalEmployee;
        task.Status = TaskStatus.PendingConfirmation;
        task.AgentExecutionStatus = AgentTaskExecutionStatus.AwaitingConfirmation;

        task.ApplyReviewDecision(approved: true);

        Assert.Equal(TaskStatus.Completed, task.Status);
        Assert.Equal(100, task.Progress);
        Assert.True(task.IsCompleted);
        Assert.Equal(AgentTaskExecutionStatus.Completed, task.AgentExecutionStatus);
    }

    [Fact]
    public void RejectingDigitalEmployeeResult_RequeuesTaskForRework()
    {
        var task = CreateTask();
        task.AssigneeType = TaskAssigneeType.DigitalEmployee;
        task.Status = TaskStatus.PendingConfirmation;
        task.IsCompleted = true;
        task.Progress = 100;
        task.AgentExecutionStatus = AgentTaskExecutionStatus.AwaitingConfirmation;

        task.ApplyReviewDecision(approved: false);

        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.False(task.IsCompleted);
        Assert.Equal(99, task.Progress);
        Assert.Equal(1, task.ReworkCount);
        Assert.Equal(AgentTaskExecutionStatus.Pending, task.AgentExecutionStatus);
    }

    [Theory]
    [InlineData(TaskStatus.NotStarted)]
    [InlineData(TaskStatus.InProgress)]
    [InlineData(TaskStatus.PendingConfirmation)]
    [InlineData(TaskStatus.Cancelled)]
    public void NonCompletedStatus_CannotKeepOneHundredPercentProgress(TaskStatus status)
    {
        var task = CreateTask();
        task.Progress = 100;
        task.IsCompleted = true;

        task.SetStatus(status);

        Assert.Equal(status, task.Status);
        Assert.Equal(99, task.Progress);
        Assert.False(task.IsCompleted);
    }

    private static ToDoTask CreateTask() => new()
    {
        CreatorId = 1,
        ProjectId = 1,
        Title = "认领规则测试",
        AssigneeType = TaskAssigneeType.Human,
        Status = TaskStatus.NotStarted
    };
}

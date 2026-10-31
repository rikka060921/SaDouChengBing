using ToDo.Entities;

namespace ToDo.Domain;

/// <summary>Explain persisted states, never infer success from a model's response.</summary>
public static class AgentWorkPresentation
{
    public static string NextStep(AgentTaskExecutionStatus status) => status switch
    {
        AgentTaskExecutionStatus.Pending => "已进入执行队列，等待后台处理，无需重复建任务。",
        AgentTaskExecutionStatus.Running => "正在处理，可以离开页面稍后查看结果。",
        AgentTaskExecutionStatus.Retrying => "系统正在重试，暂不需要重复提交；可查看执行记录了解原因。",
        AgentTaskExecutionStatus.WaitingApproval => "遇到需要授权的操作，等待有权限的人审批；该操作尚未执行。",
        AgentTaskExecutionStatus.AwaitingDispatchConfirmation => "尚未开始执行，需要确认执行人；请查看下方的调度建议。",
        AgentTaskExecutionStatus.AwaitingPlanConfirmation => "已有执行计划，尚未正式执行；请在下方确认或提出调整意见。",
        AgentTaskExecutionStatus.AwaitingConfirmation => "已有交付等待验收，请先查看成果和验收依据，再决定通过或返工。",
        AgentTaskExecutionStatus.Completed => "本次执行已结束。请结合下方交付内容、验收记录和任务状态查看结果。",
        AgentTaskExecutionStatus.Failed => "本次执行未完成。先查看错误与已有结果，再决定重试，不必重复创建任务。",
        AgentTaskExecutionStatus.Paused => "执行已暂停，请查看暂停原因；恢复前不会继续推进。",
        _ => "尚无执行记录。请先确认任务已委托给可用的数字员工。"
    };
}

public sealed record MeetingCloseoutSummary(int NewTasks, int Updates, int MissingDefinition, int Confirmed)
{
    public static MeetingCloseoutSummary From(IEnumerable<MeetingActionItem> actions)
    {
        var all = actions.ToList();
        var pending = all.Where(a => !a.IsConfirmed && a.SyncStatus.StartsWith("待确认", StringComparison.Ordinal)).ToList();
        static bool IsUpdate(MeetingActionItem a) => a.ActionType == "TaskChange" || a.MatchedTaskId.HasValue;
        return new(pending.Count(a => !IsUpdate(a)), pending.Count(IsUpdate),
            pending.Count(a => IsUpdate(a) ? !a.MatchedTaskId.HasValue : !a.AssigneeId.HasValue || !a.Deadline.HasValue), all.Count(a => a.IsConfirmed));
    }
}

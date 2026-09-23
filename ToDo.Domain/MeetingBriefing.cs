namespace ToDo.Domain;

public sealed class MeetingBriefing
{
    public int ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public MeetingBriefingPreviousMeeting? PreviousMeeting { get; init; }
    public List<MeetingBriefingCompletedTask> CompletedTasksSinceLastMeeting { get; init; } = new();  // 新增
    public List<MeetingBriefingTask> OpenTasks { get; init; } = new();
    public List<MeetingBriefingTask> OpenTasksExcludingPreviousMeeting { get; init; } = new();  // 修改：排除上一场会议的任务
}

public sealed class MeetingBriefingPreviousMeeting
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string MeetingDate { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public List<MeetingBriefingActionItem> ActionItems { get; init; } = new();
    public bool HasConfirmedTasks { get; init; }

    /// <summary>会议纪要是否已确认提交（确认写入任务或确认无新任务）</summary>
    public bool IsConfirmed { get; init; }
}

public sealed class MeetingBriefingActionItem
{
    public string Content { get; init; } = string.Empty;
    public string Assignee { get; init; } = "未识别";
    public string Deadline { get; init; } = "未设置";
    public string TaskStatus { get; init; } = "未关联任务";
    public int? TaskId { get; init; }
}

/// <summary>
/// 上次会议至今已完成的任务
/// </summary>
public sealed class MeetingBriefingCompletedTask
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Assignee { get; init; } = "未分配";
    public string AssigneeType { get; init; } = "人员";
    public DateTime? CompletedAt { get; init; }  // 完成时间
    public string CompletionDate { get; init; } = string.Empty;  // 格式化日期
}

public sealed class MeetingBriefingTask
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Assignee { get; init; } = "未分配";
    public string AssigneeType { get; init; } = "人员";
    public string Status { get; init; } = string.Empty;
    public string Priority { get; init; } = string.Empty;
    public string Deadline { get; init; } = "未设置";
    public DateTime? DeadlineAt { get; init; }
    public bool IsSubTask { get; init; }
    public string? ParentTaskTitle { get; init; }
}

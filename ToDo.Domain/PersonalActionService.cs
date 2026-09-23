using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;
using TaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Domain;

public sealed record PersonalActionLink(string Label, string Url);
public sealed record PersonalActionSignal(string Key, string Title, string ProjectName, string Reason,
    string WhyMe, DateTime CreatedAt, DateTime? Deadline, bool Critical, PersonalActionLink Action);
public sealed record PersonalActionCard(string Key, string Title, string ProjectName, int Priority,
    DateTime CreatedAt, DateTime? Deadline, IReadOnlyList<string> Reasons, IReadOnlyList<string> WhyMe,
    IReadOnlyList<PersonalActionLink> Actions)
{
    public string PriorityLabel => Priority switch { 0 => "优先处理", 1 => "已逾期", 2 => "今天处理", _ => "待处理" };
}

/// <summary>只读派生清单：权限过滤后按业务对象合并，不另外维护一套待办状态。</summary>
public sealed class PersonalActionService(ApplicationDbContext context, ApprovalRequestService approvals)
{
    public async Task<IReadOnlyList<PersonalActionCard>> GetAsync(ApplicationUser user, DateTime now,
        CancellationToken ct = default)
    {
        if (user.IsDeleted || user.Status != UserStatus.Active) return [];
        var systemAdmin = user.Role == UserRole.systemAdmin;
        var projects = await context.Project.AsNoTracking().Where(p => !p.IsDeleted
            && (systemAdmin || p.LeaderUserId == user.Id
                || context.ProjectUsers.Any(m => m.ProjectId == p.Id && m.UserId == user.Id)))
            .ToListAsync(ct);
        var projectIds = projects.Select(p => p.Id).ToList();
        var activeIds = projects.Where(p => p.Status == ProjectStatus.Active).Select(p => p.Id).ToHashSet();
        var adminIds = await context.ProjectUsers.AsNoTracking()
            .Where(m => projectIds.Contains(m.ProjectId) && m.UserId == user.Id && m.ProjectRole == (int)ProjectRole.Admin)
            .Select(m => m.ProjectId).ToListAsync(ct);
        var managed = projects.Where(p => systemAdmin || p.LeaderUserId == user.Id || adminIds.Contains(p.Id))
            .Select(p => p.Id).ToHashSet();
        var names = projects.ToDictionary(p => p.Id, p => p.Name);
        var tasks = await context.ToDoTasks.AsNoTracking().Where(t => projectIds.Contains(t.ProjectId)
            && !t.IsDeleted && !t.IsCompleted && t.Status != TaskStatus.Completed && t.Status != TaskStatus.Cancelled)
            .ToListAsync(ct);
        var taskMap = tasks.ToDictionary(t => t.Id);
        var taskIds = taskMap.Keys.ToList();
        var signals = new List<PersonalActionSignal>();
        void TaskSignal(ToDoTask task, string reason, string why, DateTime created, bool critical = false,
            string label = "处理任务", string? url = null) => signals.Add(new($"task:{task.Id}", task.Title,
                names[task.ProjectId], reason, why, created, task.EndTime, critical,
                new(label, url ?? $"/Tasks/Details/{task.Id}")));
        string ManagerOr(string personal, int projectId) => managed.Contains(projectId) ? "你有该项目的管理职责" : personal;

        var work = await context.AgentWorkItems.AsNoTracking()
            .Where(w => taskIds.Contains(w.TaskId)
                && (w.Status == AgentWorkItemStatus.WaitingPlanConfirmation || w.Status == AgentWorkItemStatus.Failed))
            .ToListAsync(ct);
        var latestWorkIds = await context.AgentWorkItems.AsNoTracking().Where(w => taskIds.Contains(w.TaskId))
            .GroupBy(w => w.TaskId).Select(g => g.Max(w => w.Id)).ToListAsync(ct);
        foreach (var item in work)
        {
            var task = taskMap[item.TaskId];
            if (task.AssigneeType != TaskAssigneeType.DigitalEmployee || task.AgentDefinitionId != item.AgentDefinitionId
                || task.ProjectId != item.ProjectId) continue;
            // 后续评论可以追加 Pending 工作，但仍须等待前面的计划确认；不能用最大工作项 ID 隐藏真正的阻塞。
            // 以工作项的真实等待状态为准，任务聚合状态可能在并发领取/恢复期间暂时滞后。
            if (item.Status == AgentWorkItemStatus.WaitingPlanConfirmation
                && (managed.Contains(task.ProjectId) || task.CreatorId == user.Id || task.ReviewerId == user.Id))
                TaskSignal(task, "执行计划需要你确认后才能继续。", ManagerOr("你是任务创建人或审核人", task.ProjectId),
                    item.CreatedAt, label: "查看执行计划", url: $"/Tasks/Details/{task.Id}#execution-plan");
            if (item.Status == AgentWorkItemStatus.Failed && latestWorkIds.Contains(item.Id)
                && task.AgentExecutionStatus == AgentTaskExecutionStatus.Failed
                && (managed.Contains(task.ProjectId) || task.CreatorId == user.Id || task.ReviewerId == user.Id
                    || item.RequestedByUserId == user.Id))
                TaskSignal(task, $"自动重试仍未成功：{item.ErrorMessage}", ManagerOr("你负责这项任务的发起或验收", task.ProjectId),
                    item.UpdatedAt, true, "查看失败原因");
        }

        var dispatches = await context.AgentDispatchDecisions.AsNoTracking().Where(d => taskIds.Contains(d.TaskId)
            && (d.Status == AgentDispatchDecisionStatus.NoCandidate || d.Status == AgentDispatchDecisionStatus.PendingConfirmation))
            .ToListAsync(ct);
        foreach (var item in dispatches)
        {
            var task = taskMap[item.TaskId];
            if (task.AssigneeType != TaskAssigneeType.DigitalEmployee || task.AgentAssignmentVersion != item.DispatchVersion
                || task.ProjectId != item.ProjectId || !(managed.Contains(task.ProjectId) || task.CreatorId == user.Id)) continue;
            TaskSignal(task, item.Status == AgentDispatchDecisionStatus.NoCandidate
                ? "没有找到职责和项目授权匹配的 Agent，需要调整要求或执行者。" : "该派单需要你确认执行者。",
                ManagerOr("你创建了这项任务", task.ProjectId), item.CreatedAt,
                item.Status == AgentDispatchDecisionStatus.NoCandidate, "处理执行者");
        }

        var receipts = await context.AgentDeliveryReceipts.AsNoTracking().Where(r => taskIds.Contains(r.TaskId))
            .OrderByDescending(r => r.Id).ToListAsync(ct);
        foreach (var task in tasks.Where(t => activeIds.Contains(t.ProjectId) && t.AssigneeType == TaskAssigneeType.Human
            && t.Status == TaskStatus.InProgress && t.ReworkCount > 0
            && (t.AssigneeId == user.Id || (t.AssigneeId == null && t.CreatorId == user.Id))))
            TaskSignal(task, $"成果被打回返工，请按审核意见修改后重新提交（累计 {task.ReworkCount} 次）。",
                "你是当前执行人", task.UpdatedAt, task.ReworkCount >= 2, "查看返工意见");
        foreach (var task in tasks.Where(t => t.Status == TaskStatus.PendingConfirmation
            && TaskReviewService.CanReviewTask(t, user, managed.Contains(t.ProjectId))))
        {
            var receipt = receipts.FirstOrDefault(r => r.TaskId == task.Id && r.AgentDefinitionId == task.AgentDefinitionId);
            var reason = task.AssigneeType == TaskAssigneeType.Human ? "执行人已提交成果，请核对后验收。"
                : receipt?.AcceptanceStatus == AgentDeliveryAcceptanceStatus.PendingReview && !string.IsNullOrWhiteSpace(receipt.ReviewComment)
                    ? receipt.ReviewComment : "交付未能自动验收，需要你核对结果与证据。";
            TaskSignal(task, reason, ManagerOr("你是指定审核人", task.ProjectId), receipt?.CreatedAt ?? task.UpdatedAt,
                label: "查看成果并验收");
        }

        var pendingApprovals = await context.ApprovalRequests.AsNoTracking().Where(a => projectIds.Contains(a.ProjectId)
            && a.Status == ApprovalRequestStatus.Pending && a.ReviewMode == AgentToolReviewMode.HumanApproval).ToListAsync(ct);
        var approvalIds = pendingApprovals.Select(a => a.Id).ToList();
        var approvalTasks = await context.AgentToolCalls.AsNoTracking()
            .Where(c => c.ApprovalRequestId.HasValue && approvalIds.Contains(c.ApprovalRequestId.Value) && c.Session != null)
            .Select(c => new { ApprovalId = c.ApprovalRequestId!.Value, c.Session!.TaskId }).ToListAsync(ct);
        foreach (var item in pendingApprovals)
        {
            if (!await approvals.CanApproveRequestAsync(item, user, ct)) continue;
            var taskId = approvalTasks.FirstOrDefault(a => a.ApprovalId == item.Id)?.TaskId;
            if (taskId.HasValue && !taskMap.ContainsKey(taskId.Value)) continue;
            var task = taskId.HasValue ? taskMap[taskId.Value] : null;
            if (task != null && task.ProjectId != item.ProjectId) continue;
            signals.Add(new(task == null ? $"approval:{item.Id}" : $"task:{task.Id}", task?.Title ?? item.Summary,
                names[item.ProjectId], $"需要授权：{item.Summary}", "你有该操作的审批资格", item.RequestedAt,
                task?.EndTime, item.RiskLevel == AgentRiskLevel.High,
                new("审查敏感操作", $"/Approvals/Index?requestId={item.Id}#approval-{item.Id}")));
        }

        var actions = await context.MeetingActionItems.AsNoTracking().Include(a => a.MeetingMinutes)
            .Where(a => a.MeetingMinutes != null && !a.MeetingMinutes.IsDeleted
                && activeIds.Contains(a.MeetingMinutes.ProjectId)
                && (!a.ProjectId.HasValue || activeIds.Contains(a.ProjectId.Value))
                && !context.MeetingMinutesProjects.Any(link => link.MeetingMinutesId == a.MeetingMinutesId
                    && context.Project.Any(p => p.Id == link.ProjectId && (p.IsDeleted || p.Status == ProjectStatus.Archived)))).ToListAsync(ct);
        foreach (var group in actions.Where(a => !a.IsConfirmed && a.SyncStatus.StartsWith("待确认")
            && managed.Contains(a.MeetingMinutes!.ProjectId)).GroupBy(a => a.MeetingMinutesId))
        {
            var meeting = group.First().MeetingMinutes!;
            signals.Add(new($"meeting:{meeting.Id}", meeting.MeetingTitle, names[meeting.ProjectId],
                $"有 {group.Count()} 项会议任务建议尚未确认写入。", "你有该项目的任务写入确认权限",
                group.Min(a => a.CreatedAt), group.Min(a => a.Deadline), false,
                new("核对会议任务建议", $"/MeetingMinutes/Details/{meeting.Id}")));
        }
        foreach (var item in actions.Where(a => a.IsConfirmed && a.MeetingMinutes!.ConfirmedAt.HasValue
            && (!a.SnoozedUntil.HasValue || a.SnoozedUntil <= now)
            && (!a.NextCheckpointAt.HasValue || a.NextCheckpointAt <= now)))
        {
            var meeting = item.MeetingMinutes!;
            var task = item.MatchedTaskId.HasValue ? taskMap.GetValueOrDefault(item.MatchedTaskId.Value) : null;
            // 有关联但已结束/删除/不可访问的任务不再作为未关联任务反复催办。
            if (item.MatchedTaskId.HasValue && task == null) continue;
            if (task != null && task.ProjectId != meeting.ProjectId) continue;
            // 审批由真实的审批请求及其权限单独负责；不能再把申请人当成解锁人。
            if (task?.AgentExecutionStatus == AgentTaskExecutionStatus.WaitingApproval) continue;
            var state = MeetingActionSupervisionService.EvaluateAttention(item, task, now);
            if (!MeetingActionSupervisionService.NeedsAttention(state.Status)) continue;
            if (state.Status is MeetingActionSupervisionStatus.PendingLink or MeetingActionSupervisionStatus.NeedsDefinition
                && !managed.Contains(meeting.ProjectId) && task?.CreatorId != user.Id) continue;
            if (!(managed.Contains(meeting.ProjectId) || item.AssigneeId == user.Id
                || task?.CreatorId == user.Id || task?.ReviewerId == user.Id
                || (task?.AssigneeType == TaskAssigneeType.Human && task.AssigneeId == user.Id))) continue;
            // AI 正常工作时的临期提示不是人工待办；只有逾期/缺条件/失败才接管。
            if (task?.AssigneeType == TaskAssigneeType.DigitalEmployee && state.Status == MeetingActionSupervisionStatus.DueSoon) continue;
            signals.Add(new(task == null ? $"meeting:{meeting.Id}" : $"task:{task.Id}", task?.Title ?? meeting.MeetingTitle,
                names[meeting.ProjectId], state.Message, ManagerOr("你负责这项会议承诺", meeting.ProjectId),
                item.CreatedAt, task?.EndTime ?? item.Deadline,
                state.Status == MeetingActionSupervisionStatus.Blocked && state.EscalationLevel >= 3,
                new("查看会议承诺", $"/MeetingMinutes/Details/{meeting.Id}")));
        }

        // 普通人工任务的临期/逾期也能在同一入口处理，不复制全部任务看板。
        foreach (var task in tasks.Where(t => activeIds.Contains(t.ProjectId) && t.EndTime.HasValue && t.EndTime < now.Date.AddDays(1)
            && t.Status != TaskStatus.PendingConfirmation && t.AgentExecutionStatus != AgentTaskExecutionStatus.WaitingApproval))
        {
            if (actions.Any(a => a.IsConfirmed && a.MatchedTaskId == task.Id
                && ((a.SnoozedUntil.HasValue && a.SnoozedUntil > now) || (a.NextCheckpointAt.HasValue && a.NextCheckpointAt > now)))) continue;
            var overdue = task.EndTime < now;
            if (task.AssigneeType == TaskAssigneeType.DigitalEmployee && !overdue) continue;
            var responsible = task.AssigneeType == TaskAssigneeType.Human ? task.AssigneeId ?? task.CreatorId : task.CreatorId;
            if (responsible != user.Id && !(overdue && managed.Contains(task.ProjectId))) continue;
            TaskSignal(task, overdue ? "任务已超过截止时间，请更新进展或处理阻塞。" : "任务今天到期，请确认能否按时完成。",
                ManagerOr("你是这项任务的当前负责人", task.ProjectId), task.CreatedAt, label: "更新任务进展");
        }
        return Merge(signals, now);
    }

    public static IReadOnlyList<PersonalActionCard> Merge(IEnumerable<PersonalActionSignal> signals, DateTime now)
        => signals.GroupBy(s => s.Key).Select(group =>
        {
            var ordered = group.OrderByDescending(s => s.Critical).ThenBy(s => s.CreatedAt).ToList();
            var first = ordered[0];
            var deadline = group.Min(s => s.Deadline);
            var priority = group.Any(s => s.Critical) ? 0 : deadline < now ? 1 : deadline < now.Date.AddDays(1) ? 2 : 3;
            return new PersonalActionCard(group.Key, first.Title, first.ProjectName, priority, group.Min(s => s.CreatedAt),
                deadline, ordered.Select(s => s.Reason).Distinct().ToList(), ordered.Select(s => s.WhyMe).Distinct().ToList(),
                ordered.Select(s => s.Action).DistinctBy(a => a.Url).ToList());
        }).OrderBy(c => c.Priority).ThenBy(c => c.Deadline ?? DateTime.MaxValue).ThenBy(c => c.CreatedAt)
            .ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
}

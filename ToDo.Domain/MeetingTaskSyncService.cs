using System.Text.Json;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Context;
using ToDo.Domain.AI;
using ToDo.Entities;

namespace ToDo.Domain;

public class MeetingTaskSyncService
{
    private readonly ApplicationDbContext _context;
    private readonly IAIService _aiService;
    private readonly UserNotificationService _notifications;
    private readonly ToDoTaskDomainService _taskDomainService;
    private readonly AgentWorkQueueService _agentWorkQueue;
    private readonly ILogger<MeetingTaskSyncService> _logger;

    public MeetingTaskSyncService(
        ApplicationDbContext context,
        IAIService aiService,
        UserNotificationService notifications,
        ToDoTaskDomainService taskDomainService,
        AgentWorkQueueService agentWorkQueue,
        ILogger<MeetingTaskSyncService>? logger = null)
    {
        _context = context;
        _aiService = aiService;
        _notifications = notifications;
        _taskDomainService = taskDomainService;
        _agentWorkQueue = agentWorkQueue;
        _logger = logger ?? NullLogger<MeetingTaskSyncService>.Instance;
    }

    public async Task<MeetingSyncResult> PrepareAsync(int meetingId, int operatorId)
    {
        var meeting = await _context.MeetingMinutes
            .Include(m => m.Project)
            .FirstOrDefaultAsync(m => m.Id == meetingId && !m.IsDeleted);
        if (meeting == null)
            return MeetingSyncResult.Failed("会议纪要不存在或已被删除");

        var operatorUser = await _context.Users.FirstOrDefaultAsync(item => item.Id == operatorId);
        if (operatorUser == null || !await CanAccessMeetingAsync(meeting, operatorUser))
            return MeetingSyncResult.Failed("你没有权限解析该会议纪要");
        if (meeting.IsDraft)
            return MeetingSyncResult.Failed("草稿不能生成任务，请先正式保存会议纪要");

        // ===== 多项目改造：加载会议关联的所有项目 =====
        var meetingProjects = await _context.MeetingMinutesProjects
            .Where(mp => mp.MeetingMinutesId == meetingId)
            .Include(mp => mp.Project)
            .ToListAsync();

        // 兜底：如果关联表没有记录（历史数据），用首项目
        if (meetingProjects.Count == 0 && meeting.Project != null)
        {
            meetingProjects = new List<MeetingMinutesProject>
            {
                new MeetingMinutesProject
                {
                    MeetingMinutesId = meetingId,
                    ProjectId = meeting.ProjectId,
                    IsPrimary = true,
                    Project = meeting.Project
                }
            };
        }

        var projectNames = meetingProjects
            .Where(mp => mp.Project != null)
            .Select(mp => mp.Project!.Name)
            .Distinct()
            .ToList();

        // 项目名 → ProjectId 映射（项目名理论上唯一）
        var projectNameToId = meetingProjects
            .Where(mp => mp.Project != null)
            .GroupBy(mp => mp.Project!.Name)
            .ToDictionary(g => g.Key, g => g.First().ProjectId);

        var allowedProjectIds = meetingProjects.Select(mp => mp.ProjectId).ToHashSet();
        if (allowedProjectIds.Count == 0) allowedProjectIds.Add(meeting.ProjectId);

        // ===== 多项目改造：所有关联项目的任务分组，按项目分组加载 =====
        var projectTaskGroupsByProject = new Dictionary<int, List<string>>();
        foreach (var pid in allowedProjectIds)
        {
            projectTaskGroupsByProject[pid] = await _context.TaskGroups
                .Where(g => g.ProjectId == pid && !g.IsDeleted)
                .Select(g => g.Name)
                .ToListAsync();
        }

        // ===== 多项目改造：成员名单按所有关联项目汇总（用于 AI 匹配责任人）=====
        var memberNames = await _context.ProjectUsers
            .Where(pu => allowedProjectIds.Contains(pu.ProjectId))
            .Include(pu => pu.User)
            .Select(pu => pu.User!)
            .Where(u => u != null)
            .Select(u => u.RealName ?? u.UserName ?? "")
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .ToListAsync();

        // ===== 多项目改造：AI 调用传入项目名列表 =====
        var sourceText = string.IsNullOrWhiteSpace(meeting.TranscriptText) ? meeting.MeetingContent : meeting.TranscriptText;
        var aiFullResult = await _aiService.ProcessMeetingMinutesFullStructAsync(sourceText, memberNames, projectNames);

        // ===== 多项目改造：完整成员信息按所有关联项目加载 =====
        var members = await _context.ProjectUsers
            .Where(pu => allowedProjectIds.Contains(pu.ProjectId))
            .Include(pu => pu.User)
            .Select(pu => pu.User!)
            .Where(u => u != null)
            .Distinct()
            .ToListAsync();

        List<MeetingTaskParseItem> parseItems = new();

        // 首先保存开会原因和决策（无论是否有任务）
        if (aiFullResult.Success)
        {
            var correctedSummary = CorrectNamesInText(aiFullResult.KeyPoints, members, meeting.Project?.LeaderUserId);
            meeting.AiSummary = RemoveTodoSection(correctedSummary);

            meeting.AiMeetingPurpose = CorrectNamesInText(aiFullResult.MeetingPurpose ?? "", members, meeting.Project?.LeaderUserId);

            if (aiFullResult.Decisions != null && aiFullResult.Decisions.Any())
            {
                foreach (var decision in aiFullResult.Decisions)
                {
                    decision.DecisionMaker = CorrectNamesInText(decision.DecisionMaker ?? "", members, meeting.Project?.LeaderUserId);
                    var evidence = MeetingQuoteEvidence.Resolve(
                        sourceText,
                        decision.OriginalQuotes,
                        decision.KeyQuote,
                        decision.OriginalQuote);
                    decision.OriginalQuotes = evidence.Quotes.ToList();
                    decision.KeyQuote = evidence.KeyQuote;
                    decision.OriginalQuote = evidence.KeyQuote;
                }
                meeting.AiDecisionsJson = JsonSerializer.Serialize(aiFullResult.Decisions);
            }
            else
            {
                meeting.AiDecisionsJson = null;
            }

            parseItems = aiFullResult.TaskItems.ToList();
        }
        else
        {
            meeting.AiSummary = $"AI解析失败: {aiFullResult.ErrorMessage}";
            parseItems = new List<MeetingTaskParseItem>();
        }

        // 仅在新版 AI 调用失败时降级
        if (!aiFullResult.Success && !parseItems.Any())
        {
            var oldResult = await _aiService.ProcessMeetingMinutesAsync(sourceText);

            if (oldResult.Success && oldResult.TodoItems.Any())
            {
                parseItems = oldResult.TodoItems.Select(t => new MeetingTaskParseItem
                {
                    Title = t.Content.Length > 255 ? t.Content.Substring(0, 255) : t.Content,
                    Description = t.Content,
                    Deadline = t.Deadline?.ToString("yyyy-MM-dd"),
                    AssigneeName = t.Assignee,
                    Priority = "Medium",
                    Status = "NotStarted",
                    Group = null
                }).ToList();
            }
            else
            {
                parseItems = RuleBasedFallbackParse(sourceText);
            }
        }

        meeting.AiActionItemsJson = JsonSerializer.Serialize(parseItems);
        meeting.SubmittedAt ??= AppTime.Now;

        // 只删除待确认行动项；IsConfirmed=true 已确认条目保留
        var oldUnconfirmedItems = await _context.MeetingActionItems
            .Where(item => item.MeetingMinutesId == meetingId && !item.IsConfirmed)
            .ToListAsync();
        _context.MeetingActionItems.RemoveRange(oldUnconfirmedItems);

        // ===== 多项目改造：按项目分组加载任务 =====
        var projectTasksByProject = new Dictionary<int, List<ToDoTask>>();
        foreach (var pid in allowedProjectIds)
        {
            projectTasksByProject[pid] = await _context.ToDoTasks
                .Where(task => task.ProjectId == pid && !task.IsDeleted)
                .ToListAsync();
        }

        // 只加载本次会议相关的议题
        var meetingAgendasViaRelation = await _context.MeetingAgendaRelations
            .Where(r => r.MeetingMinutesId == meetingId && !r.MeetingAgenda.IsDeleted)
            .Select(r => r.MeetingAgenda)
            .ToListAsync();

        var legacyArchivedAgendas = await _context.MeetingAgendas
            .Where(a => allowedProjectIds.Contains(a.ProjectId)
                && !a.IsDeleted
                && a.Status == AgendaStatus.Archived
                && a.ArchivedByMeetingId == meetingId)
            .ToListAsync();

        var allMeetingAgendas = meetingAgendasViaRelation
            .Concat(legacyArchivedAgendas)
            .GroupBy(a => a.Id)
            .Select(g => g.First())
            .ToList();

        List<MeetingAgenda> agendas;
        if (allMeetingAgendas.Any())
        {
            agendas = allMeetingAgendas;
        }
        else
        {
            // ===== 多项目改造：加载所有关联项目的活跃议题 =====
            agendas = await _context.MeetingAgendas
                .Where(a => allowedProjectIds.Contains(a.ProjectId)
                    && !a.IsDeleted
                    && a.Status == AgendaStatus.Active)
                .ToListAsync();
        }

        HashSet<string> virtualAssignee = new(StringComparer.OrdinalIgnoreCase)
        {
            "全体成员", "全部成员", "项目组", "团队", "相关人员", "各负责人", "相关负责人", "所有人"
        };

        var distinctTodos = parseItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Title))
            .GroupBy(item => NormalizeTaskText($"{item.Title}||{item.Description}"))
            .Select(group => group.First())
            .ToList();

        var resultItems = new List<MeetingActionItem>();

        foreach (var todo in distinctTodos)
        {
            // ===== 多项目改造：根据 AI 返回的 ProjectName 映射 ProjectId =====
            int itemProjectId;
            if (!string.IsNullOrWhiteSpace(todo.ProjectName)
                && projectNameToId.TryGetValue(todo.ProjectName, out var pid))
            {
                itemProjectId = pid;
            }
            else
            {
                // AI 没判断出或名字对不上，兜底用首项目
                itemProjectId = meeting.ProjectId;
            }

            // ===== 多项目改造：取该项目的任务列表用于匹配 =====
            var projectTasks = projectTasksByProject.TryGetValue(itemProjectId, out var list)
                ? list
                : new List<ToDoTask>();

            var assignee = FindAssigneeForTodo(members, todo, meeting.Project?.LeaderUserId);

            string? finalAssigneeText;
            if (assignee != null)
            {
                finalAssigneeText = assignee.RealName;
            }
            else
            {
                finalAssigneeText = null;
            }

            var correctedTitle = CorrectNamesInText(todo.Title, members, meeting.Project?.LeaderUserId);
            var correctedDescription = CorrectNamesInText(todo.Description ?? string.Empty, members, meeting.Project?.LeaderUserId);

            var taskTitle = correctedTitle.Length > 255 ? correctedTitle[..255] : correctedTitle;
            var fullTaskContent = $"来源会议：{meeting.MeetingTitle}\n任务：{correctedTitle}\n详情：{correctedDescription}";

            var matchedTask = FindMatchingTask(projectTasks, correctedTitle, correctedDescription, fullTaskContent);

            var matchedAgendaId = MatchAgenda(correctedTitle, correctedDescription, agendas);

            int? sourceDecisionIndex = null;
            string? sourceDecisionContent = null;
            if (aiFullResult.Success
                && todo.SourceDecisionIndex.HasValue
                && aiFullResult.Decisions != null
                && todo.SourceDecisionIndex.Value >= 1
                && todo.SourceDecisionIndex.Value <= aiFullResult.Decisions.Count)
            {
                var sourceDecision = aiFullResult.Decisions[todo.SourceDecisionIndex.Value - 1];
                if (sourceDecision != null && !string.IsNullOrWhiteSpace(sourceDecision.Content))
                {
                    sourceDecisionIndex = todo.SourceDecisionIndex.Value;
                    sourceDecisionContent = sourceDecision.Content.Length > 500
                        ? sourceDecision.Content[..500]
                        : sourceDecision.Content;
                }
            }

            // ===== 多项目改造：分组校验按行动项所属项目 =====
            string? validGroupName = null;
            if (!string.IsNullOrWhiteSpace(todo.Group))
            {
                var aiGroupText = todo.Group.Trim();
                if (projectTaskGroupsByProject.TryGetValue(itemProjectId, out var groupNames)
                    && groupNames.Any(g => string.Equals(g, aiGroupText, StringComparison.OrdinalIgnoreCase)))
                {
                    validGroupName = aiGroupText;
                }
            }

            DateTime? deadlineDate = null;
            if (!string.IsNullOrWhiteSpace(todo.Deadline) && DateTime.TryParse(todo.Deadline, out var dt))
            {
                deadlineDate = dt;
            }

            var actionType = "NewTask";
            string? beforeStatus = null;
            string? afterStatus = todo.Status;
            int? beforeAssigneeId = null;
            int? afterAssigneeId = assignee?.Id;
            DateTime? beforeDeadline = null;
            DateTime? afterDeadline = deadlineDate;
            string? beforePriority = null;
            string? afterPriority = todo.Priority;
            string? changeDescription = null;

            if (matchedTask != null)
            {
                actionType = "TaskChange";

                beforeStatus = matchedTask.Status.ToString();
                beforeAssigneeId = matchedTask.AssigneeId;
                beforeDeadline = matchedTask.EndTime;
                beforePriority = matchedTask.Priority.ToString();

                var changeText = $"{correctedTitle} {correctedDescription}";
                afterStatus = HasExplicitStatusChange(changeText) ? todo.Status : beforeStatus;
                afterAssigneeId = assignee?.Id ?? beforeAssigneeId;
                afterDeadline = deadlineDate ?? beforeDeadline;
                afterPriority = HasExplicitPriorityChange(changeText) ? todo.Priority : beforePriority;

                var changes = new List<string>();
                if (beforeStatus != afterStatus)
                    changes.Add($"状态: {beforeStatus} → {afterStatus}");
                if (beforeAssigneeId != afterAssigneeId)
                    changes.Add("负责人变更");
                if (beforeDeadline?.ToString("yyyy-MM-dd") != afterDeadline?.ToString("yyyy-MM-dd"))
                    changes.Add($"截止时间: {beforeDeadline?.ToString("yyyy-MM-dd") ?? "未设置"} → {afterDeadline?.ToString("yyyy-MM-dd") ?? "未设置"}");
                if (beforePriority != afterPriority)
                    changes.Add($"优先级: {beforePriority} → {afterPriority}");

                changeDescription = changes.Count > 0 ? string.Join("；", changes) : null;
            }

            var item = new MeetingActionItem
            {
                MeetingMinutesId = meetingId,
                ProjectId = itemProjectId,   // ===== 多项目改造：写入归属项目 =====
                Title = correctedTitle,
                Description = correctedDescription,
                Content = $"{correctedTitle} {correctedDescription}",
                Priority = matchedTask == null ? todo.Priority : afterPriority ?? beforePriority ?? "Medium",
                GroupName = validGroupName,
                TaskStatus = matchedTask == null ? todo.Status : afterStatus ?? beforeStatus ?? "NotStarted",
                AssigneeText = finalAssigneeText,
                AssigneeId = matchedTask == null ? assignee?.Id : afterAssigneeId,
                Deadline = matchedTask == null ? deadlineDate : afterDeadline,
                MatchedTaskId = matchedTask?.Id,
                MeetingAgendaId = matchedAgendaId,
                SourceDecisionIndex = sourceDecisionIndex,
                SourceDecisionContent = sourceDecisionContent,

                ActionType = actionType,
                BeforeStatus = beforeStatus,
                AfterStatus = afterStatus,
                BeforeAssigneeId = beforeAssigneeId,
                AfterAssigneeId = afterAssigneeId,
                BeforeDeadline = beforeDeadline,
                AfterDeadline = afterDeadline,
                BeforePriority = beforePriority,
                AfterPriority = afterPriority,
                ChangeDescription = changeDescription,

                SyncStatus = matchedTask == null ? "待确认创建" : "待确认更新",
                SyncMessage = matchedTask == null
                    ? $"确认后将创建【{todo.Priority}优先级】任务，分组：{(validGroupName ?? "无分组")}"
                    : $"确认后更新已有任务「{matchedTask.Title}」，变更内容：{(changeDescription ?? "无")}"
            };
            _context.MeetingActionItems.Add(item);
            resultItems.Add(item);
        }

        foreach (var pItem in parseItems)
        {
            var name = pItem.AssigneeName?.Trim();
            if (!string.IsNullOrWhiteSpace(name) && virtualAssignee.Contains(name))
            {
                pItem.AssigneeName = null;
            }
        }
        meeting.AiActionItemsJson = JsonSerializer.Serialize(parseItems);

        await _context.SaveChangesAsync();

        if (resultItems.Count > 0)
        {
            await _notifications.NotifyManyAsync(
                (await new AttentionRecipientService(_context).ManagersAsync(meeting.ProjectId)).Take(1),
                "会议行动项待确认",
                $"会议「{meeting.MeetingTitle}」生成了 {resultItems.Count} 项结构化任务建议，包含优先级、分组、截止时间，请确认后写入项目。",
                "Approval",
                $"/MeetingMinutes/Details/{meeting.Id}");
        }

        return MeetingSyncResult.Successful(meeting.AiSummary ?? string.Empty, resultItems);
    }

    public async Task<MeetingSyncResult> ConfirmAsync(int meetingId, int operatorId)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var meeting = await _context.MeetingMinutes
            .Include(item => item.Project)
            .FirstOrDefaultAsync(item => item.Id == meetingId && !item.IsDeleted);
        if (meeting == null)
            return MeetingSyncResult.Failed("会议纪要不存在或已被删除");

        var operatorUser = await _context.Users.FirstOrDefaultAsync(item => item.Id == operatorId);
        if (operatorUser == null || !await CanApproveWritesAsync(meeting, operatorUser))
            return MeetingSyncResult.Failed("仅系统管理员、项目负责人或该项目管理员可以确认任务写入");

        // ===== 多项目改造：加载会议关联的所有项目 =====
        var meetingProjects = await _context.MeetingMinutesProjects
            .Where(mp => mp.MeetingMinutesId == meetingId)
            .ToListAsync();
        var allowedProjectIds = meetingProjects.Select(mp => mp.ProjectId).ToHashSet();
        if (allowedProjectIds.Count == 0) allowedProjectIds.Add(meeting.ProjectId);

        var actionItems = await _context.MeetingActionItems
            .Include(item => item.Assignee)
            .Where(item => item.MeetingMinutesId == meetingId && item.SyncStatus.StartsWith("待确认"))
            .OrderBy(item => item.Id)
            .ToListAsync();

        var alreadyConfirmed = meeting.ConfirmedAt.HasValue && actionItems.Count == 0;

        // ===== 多项目改造：加载所有关联项目的任务 =====
        var projectTasks = await _context.ToDoTasks
            .Where(task => allowedProjectIds.Contains(task.ProjectId) && !task.IsDeleted)
            .ToListAsync();

        var projectTaskIds = projectTasks.Select(task => task.Id).ToHashSet();
        var invalidChangeItem = actionItems.FirstOrDefault(item =>
        {
            if (item.ActionType != "TaskChange") return false;
            var effectivePid = item.ProjectId ?? meeting.ProjectId;
            if (!item.MatchedTaskId.HasValue) return true;
            var t = projectTasks.FirstOrDefault(x => x.Id == item.MatchedTaskId.Value);
            return t == null || t.ProjectId != effectivePid;
        });
        if (invalidChangeItem != null)
            return MeetingSyncResult.Failed($"行动项“{invalidChangeItem.Title}”关联的任务不存在、已删除或不属于对应项目，请重新解析后再确认");

        // 会议草案可能生成于旧状态，不能用过期的验收意见覆盖后来发生的返工/执行。
        foreach (var change in actionItems.Where(i => i.ActionType == "TaskChange"
            && !string.IsNullOrWhiteSpace(i.AfterStatus) && i.BeforeStatus != i.AfterStatus))
        {
            var task = projectTasks.Single(t => t.Id == change.MatchedTaskId);
            if (!Enum.TryParse<ToDo.Entities.TaskStatus>(change.AfterStatus, out var nextStatus)
                || !Enum.IsDefined(nextStatus))
                return MeetingSyncResult.Failed($"行动项“{change.Title}”的任务状态无效，请重新检查");
            if (!string.IsNullOrWhiteSpace(change.BeforeStatus)
                && task.Status.ToString() != change.BeforeStatus && task.Status != nextStatus)
                return MeetingSyncResult.Failed($"任务“{task.Title}”的状态已变化，请重新解析并确认，避免覆盖最新进展");
            if (task.AssigneeType == TaskAssigneeType.DigitalEmployee
                && nextStatus == ToDo.Entities.TaskStatus.Completed
                && task.Status is not (ToDo.Entities.TaskStatus.PendingConfirmation or ToDo.Entities.TaskStatus.Completed))
                return MeetingSyncResult.Failed($"Agent 任务“{task.Title}”尚未提交待验收结果，不能直接标记完成");
        }

        if (!alreadyConfirmed)
        {
            meeting.IsDraft = false;
            meeting.SubmittedAt = AppTime.Now;
            meeting.ConfirmedAt = AppTime.Now;
            meeting.LastModifiedAt = AppTime.Now;
        }

        foreach (var item in actionItems)
        {
            // ===== 多项目改造：行动项归属项目（回退首项目）=====
            var effectiveProjectId = item.ProjectId ?? meeting.ProjectId;

            if (item.ActionType == "TaskChange" && !item.MatchedTaskId.HasValue)
            {
                item.SyncStatus = "同步失败";
                item.SyncMessage = "任务变更缺少匹配任务，请重新解析或改为新建任务";
                continue;
            }

            if (item.ActionType == "TaskChange" && item.MatchedTaskId.HasValue)
            {
                // ===== 多项目改造：校验项目归属并取出任务 =====
                var matchedTask = await _context.ToDoTasks.FirstOrDefaultAsync(task =>
                    task.Id == item.MatchedTaskId.Value
                    && task.ProjectId == effectiveProjectId
                    && !task.IsDeleted);
                if (matchedTask != null)
                {
                    var beforeStatus = matchedTask.Status.ToString();
                    var beforeAssigneeId = matchedTask.AssigneeId;
                    var beforeDeadline = matchedTask.EndTime;
                    var beforePriority = matchedTask.Priority.ToString();
                    var beforeProjectId = matchedTask.ProjectId;

                    if (!string.IsNullOrWhiteSpace(item.AfterStatus)
                        && !string.Equals(item.BeforeStatus, item.AfterStatus, StringComparison.Ordinal))
                    {
                        var nextStatus = Enum.Parse<ToDo.Entities.TaskStatus>(item.AfterStatus);
                        if (matchedTask.Status == ToDo.Entities.TaskStatus.PendingConfirmation
                            && nextStatus is ToDo.Entities.TaskStatus.Completed or ToDo.Entities.TaskStatus.InProgress)
                        {
                            var approved = nextStatus == ToDo.Entities.TaskStatus.Completed;
                            var comment = $"会议「{meeting.MeetingTitle}」{(approved ? "验收通过" : "打回返工")}。{item.ChangeDescription}";
                            var review = await new TaskReviewService(_context).StageReviewAsync(matchedTask, operatorUser, approved, comment);
                            if (review == null)
                                throw new InvalidOperationException("会议行动项对应任务无法审核，请刷新后重试");

                            if (!approved && matchedTask.AssigneeType == TaskAssigneeType.DigitalEmployee)
                            {
                                await _context.SaveChangesAsync();
                                await _agentWorkQueue.EnqueueTaskAsync(
                                    matchedTask.Id, operatorId, AgentWorkTriggerType.TaskReviewRejected, review.Id,
                                    $"任务审核被打回，请根据会议审核意见返工并重新提交可核验结果：\n{review.Content}",
                                    $"task-review-rejected:{matchedTask.Id}:{review.Id}:{matchedTask.AgentDefinitionId}");
                            }
                        }
                        else
                        {
                            matchedTask.SetStatus(nextStatus);
                        }

                        if (nextStatus == ToDo.Entities.TaskStatus.Completed)
                            await ArchiveReviewedTaskAgendasAsync(matchedTask, meeting);
                    }

                    if (item.BeforeAssigneeId != item.AfterAssigneeId)
                    {
                        matchedTask.AssigneeId = item.AfterAssigneeId;
                    }

                    if (item.BeforeDeadline != item.AfterDeadline)
                    {
                        matchedTask.EndTime = item.AfterDeadline;
                    }

                    if (!string.IsNullOrWhiteSpace(item.AfterPriority)
                        && !string.Equals(item.BeforePriority, item.AfterPriority, StringComparison.Ordinal))
                    {
                        matchedTask.Priority = item.AfterPriority switch
                        {
                            "High" => TaskPriority.High,
                            "Low" => TaskPriority.Low,
                            _ => TaskPriority.Medium
                        };
                    }

                    // ===== 多项目改造：分组用行动项的项目 =====
                    if (!string.IsNullOrWhiteSpace(item.GroupName))
                    {
                        var groupId = await GetTaskGroupIdByNameAsync(effectiveProjectId, item.GroupName);
                        matchedTask.GroupId = groupId;
                    }

                    if (!(matchedTask.Description?.Contains($"来源会议：{meeting.MeetingTitle}", StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        matchedTask.Description = $"{matchedTask.Description}\n\n来源会议：{meeting.MeetingTitle}";
                    }

                    matchedTask.UpdatedAt = AppTime.Now;
                    item.SyncStatus = "已更新";
                    item.SyncMessage = "审批通过，已更新匹配到的项目任务";

                    await _taskDomainService.LogTaskOperationAsync(
                        OperationType.更新,
                        OperationTarget.任务,
                        operatorId,
                        afterState: $"会议行动项更新任务：{matchedTask.Title}",
                        projectId: matchedTask.ProjectId,
                        taskId: matchedTask.Id,
                        taskTitle: matchedTask.Title,
                        targetId: matchedTask.Id,
                        status: OperationStatus.成功);

                    await AddTaskChangeAuditLogAsync(
                        matchedTask,
                        "更新",
                        operatorId,
                        beforeStatus,
                        matchedTask.Status.ToString(),
                        beforeAssigneeId,
                        matchedTask.AssigneeId,
                        beforeDeadline,
                        matchedTask.EndTime,
                        beforePriority,
                        matchedTask.Priority.ToString(),
                        beforeProjectId);
                }
                else
                {
                    item.SyncStatus = "同步失败";
                    item.SyncMessage = "匹配的正式任务已不存在，请重新解析或改为新建任务";
                    continue;
                }
            }
            else
            {
                // ===== 多项目改造：新增任务写到行动项所属项目 =====
                var content = item.Content.Trim();
                var taskTitle = content.Length > 255 ? content[..255] : content;
                var sourceMarker = $"来源会议：{meeting.MeetingTitle}\n{content}";

                // 该项目的任务列表
                var projectTasksForItem = projectTasks
                    .Where(t => t.ProjectId == effectiveProjectId)
                    .ToList();

                var matchedTask = item.MatchedTaskId.HasValue
                    ? projectTasksForItem.FirstOrDefault(task => task.Id == item.MatchedTaskId.Value)
                    : null;
                matchedTask ??= FindMatchingTask(projectTasksForItem, taskTitle, item.Description ?? string.Empty, sourceMarker);

                if (matchedTask == null)
                {
                    matchedTask = new ToDoTask
                    {
                        Title = string.IsNullOrWhiteSpace(item.Title)
                            ? item.Content.Length > 255 ? item.Content[..255] : item.Content
                            : item.Title.Length > 255 ? item.Title[..255] : item.Title,
                        Description = $"{item.Description}\n\n来源会议：{meeting.MeetingTitle}",
                        ProjectId = effectiveProjectId,   // ===== 多项目改造：归属项目 =====
                        CreatorId = operatorId,
                        AssigneeId = item.AssigneeId,
                        ReviewerId = null,   // 多项目改造：不清楚具体负责人，统一置 null，由后续流程决定
                        EndTime = item.Deadline,
                        Priority = item.Priority switch
                        {
                            "High" => TaskPriority.High,
                            "Low" => TaskPriority.Low,
                            _ => TaskPriority.Medium
                        },
                        Status = item.TaskStatus switch
                        {
                            "Completed" => ToDo.Entities.TaskStatus.Completed,
                            "InProgress" => ToDo.Entities.TaskStatus.InProgress,
                            "PendingConfirmation" => ToDo.Entities.TaskStatus.PendingConfirmation,
                            "Cancelled" => ToDo.Entities.TaskStatus.Cancelled,
                            _ => ToDo.Entities.TaskStatus.NotStarted
                        },
                        GroupId = await GetTaskGroupIdByNameAsync(effectiveProjectId, item.GroupName)
                    };
                    matchedTask.SetStatus(matchedTask.Status);
                    _context.ToDoTasks.Add(matchedTask);
                    await _context.SaveChangesAsync();
                    projectTasks.Add(matchedTask);
                    item.SyncStatus = "已创建";
                    item.SyncMessage = "审批通过，已根据会议行动项创建任务";
                    item.MatchedTaskId = matchedTask.Id;

                    await _taskDomainService.LogTaskOperationAsync(
                        OperationType.创建,
                        OperationTarget.任务,
                        operatorId,
                        afterState: $"会议行动项经人工确认后创建任务：{matchedTask.Title}",
                        projectId: matchedTask.ProjectId,
                        taskId: matchedTask.Id,
                        taskTitle: matchedTask.Title,
                        targetId: matchedTask.Id,
                        status: OperationStatus.成功);

                    await AddTaskChangeAuditLogAsync(
                        matchedTask,
                        "创建",
                        operatorId,
                        beforeStatus: null,
                        afterStatus: matchedTask.Status.ToString(),
                        beforeAssigneeId: null,
                        afterAssigneeId: matchedTask.AssigneeId,
                        beforeDeadline: null,
                        afterDeadline: matchedTask.EndTime,
                        beforePriority: null,
                        afterPriority: matchedTask.Priority.ToString(),
                        projectId: matchedTask.ProjectId);
                }
            }

            item.IsConfirmed = true;

            await _notifications.NotifyManyAsync(
                new[] { meeting.CreatorId, item.AssigneeId ?? 0, meeting.Project?.LeaderUserId ?? 0 },
                "会议行动项已确认",
                $"会议「{meeting.MeetingTitle}」的行动项已处理：{item.Title}",
                "Task",
                $"/Tasks/Details/{item.MatchedTaskId}");
        }

        await _context.SaveChangesAsync();
        await transaction.CommitAsync();

        // ===== 议题归档检查 =====
        try
        {
            var confirmedActionItems = await _context.MeetingActionItems
                .Where(ai => ai.MeetingMinutesId == meeting.Id && ai.IsConfirmed)
                .ToListAsync();

            // ===== 多项目改造：议题归属所有关联项目 =====
            var involvedProjectIds = actionItems
                .Select(ai => ai.ProjectId ?? meeting.ProjectId)
                .Distinct()
                .ToList();
            if (involvedProjectIds.Count == 0) involvedProjectIds.Add(meeting.ProjectId);

            var agendas = await _context.MeetingAgendas
                .Where(a => involvedProjectIds.Contains(a.ProjectId)
                    && !a.IsDeleted
                    && a.Status == AgendaStatus.Active)
                .ToListAsync();

            if (agendas.Any())
            {
                var newRelations = new List<MeetingAgendaRelation>();

                var relatedAgendaIds = (await _context.MeetingAgendaRelations
                    .Where(r => r.MeetingMinutesId == meeting.Id)
                    .Select(r => r.MeetingAgendaId)
                    .ToListAsync())
                    .ToHashSet();

                // ===== 多项目改造：待审核任务也按多个项目加载 =====
                var archivableTasks = await _context.ToDoTasks
                    .Where(t => involvedProjectIds.Contains(t.ProjectId)
                        && !t.IsDeleted
                        && t.Status == ToDo.Entities.TaskStatus.PendingConfirmation)
                    .ToListAsync();

                var archivableTaskById = archivableTasks.ToDictionary(t => t.Id);
                var archivableTaskIdSet = archivableTaskById.Keys.ToHashSet();
                var archivableTaskTitleMap = archivableTasks.ToDictionary(t => t.Id, t => t.Title);

                var meetingText = $"{meeting.TranscriptText ?? meeting.MeetingContent ?? string.Empty}\n{meeting.AiSummary ?? string.Empty}";

                var actionItemByTaskId = confirmedActionItems
                    .Where(ai => ai.MatchedTaskId.HasValue)
                    .GroupBy(ai => ai.MatchedTaskId!.Value)
                    .ToDictionary(g => g.Key, g => g.First());

                var discussedAgendaIds = confirmedActionItems
                    .Where(ai => ai.MeetingAgendaId.HasValue)
                    .Select(ai => ai.MeetingAgendaId.GetValueOrDefault())
                    .ToHashSet();

                var aiCandidateAgendas = new List<MeetingAgenda>();

                foreach (var agenda in agendas)
                {
                    if (!agenda.SourceId.HasValue || !archivableTaskIdSet.Contains(agenda.SourceId.Value))
                        continue;

                    MeetingActionItem? relatedItem = null;
                    if (discussedAgendaIds.Contains(agenda.Id))
                    {
                        relatedItem = confirmedActionItems
                            .Where(ai => ai.MeetingAgendaId == agenda.Id)
                            .OrderBy(ai => ai.Id)
                            .First();
                    }
                    else if (actionItemByTaskId.TryGetValue(agenda.SourceId.Value, out var byTask))
                    {
                        relatedItem = byTask;
                    }

                    if (relatedItem == null)
                    {
                        aiCandidateAgendas.Add(agenda);
                        continue;
                    }

                    if (!relatedAgendaIds.Contains(agenda.Id))
                    {
                        newRelations.Add(new MeetingAgendaRelation
                        {
                            MeetingMinutesId = meeting.Id,
                            MeetingAgendaId = agenda.Id,
                            CreatedAt = AppTime.Now
                        });
                    }

                    bool isRejected = relatedItem.ActionType == "TaskChange"
                        && relatedItem.BeforeStatus == "PendingConfirmation"
                        && !string.IsNullOrEmpty(relatedItem.AfterStatus)
                        && relatedItem.AfterStatus != "PendingConfirmation";

                    bool noChange = relatedItem.ActionType != "TaskChange"
                        || string.IsNullOrEmpty(relatedItem.ChangeDescription);

                    bool stillPending = relatedItem.AfterStatus == "PendingConfirmation";

                    if (!isRejected && (noChange || stillPending))
                    {
                        if (!archivableTaskById.TryGetValue(agenda.SourceId!.Value, out var pendingTask)
                            || !await new TaskReviewService(_context).StageAgendaApprovalAsync(
                                pendingTask, operatorUser, $"会议「{meeting.MeetingTitle}」确认验收并归档议题")) continue;
                        agenda.Status = AgendaStatus.Archived;
                        agenda.ArchivedAt = AppTime.Now;
                        agenda.ArchivedByMeetingId = meeting.Id;
                        agenda.LastModifiedAt = AppTime.Now;
                        _context.MeetingAgendas.Update(agenda);
                    }
                }

                if (aiCandidateAgendas.Any())
                {
                    var candidateTasks = aiCandidateAgendas
                        .Select(a => a.SourceId!.Value)
                        .Distinct()
                        .Where(id => archivableTaskTitleMap.ContainsKey(id))
                        .Select(id => (TaskId: id, Title: archivableTaskTitleMap[id]))
                        .ToList();

                    var judgements = await JudgeTasksByMeetingContentAsync(meetingText, candidateTasks);

                    foreach (var agenda in aiCandidateAgendas)
                    {
                        bool archiveAgenda;
                        var judge = judgements?.FirstOrDefault(j => j.TaskId == agenda.SourceId!.Value);
                        if (judge != null)
                        {
                            archiveAgenda = judge.Discussed && !judge.Rejected && !judge.Changed;
                        }
                        else
                        {
                            archiveAgenda = false;
                        }

                        if (archiveAgenda)
                        {
                            if (!archivableTaskById.TryGetValue(agenda.SourceId!.Value, out var pendingTask)
                                || !await new TaskReviewService(_context).StageAgendaApprovalAsync(
                                    pendingTask, operatorUser, $"会议「{meeting.MeetingTitle}」确认验收并归档议题")) continue;
                            if (!relatedAgendaIds.Contains(agenda.Id))
                            {
                                newRelations.Add(new MeetingAgendaRelation
                                {
                                    MeetingMinutesId = meeting.Id,
                                    MeetingAgendaId = agenda.Id,
                                    CreatedAt = AppTime.Now
                                });
                            }

                            agenda.Status = AgendaStatus.Archived;
                            agenda.ArchivedAt = AppTime.Now;
                            agenda.ArchivedByMeetingId = meeting.Id;
                            agenda.LastModifiedAt = AppTime.Now;
                            _context.MeetingAgendas.Update(agenda);
                        }
                    }
                }

                if (newRelations.Any())
                {
                    _context.MeetingAgendaRelations.AddRange(newRelations);
                }

                await _context.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Meeting {MeetingId} was confirmed, but agenda archival failed and will be retried on the next confirmation request",
                meeting.Id);
        }

        return MeetingSyncResult.Successful(meeting.AiSummary ?? string.Empty, actionItems);
    }

    private async Task ArchiveReviewedTaskAgendasAsync(ToDoTask task, MeetingMinutes meeting)
    {
        var agendas = await _context.MeetingAgendas.Where(a => a.ProjectId == task.ProjectId
            && a.SourceId == task.Id && !a.IsDeleted && a.Status == AgendaStatus.Active).ToListAsync();
        var relatedIds = (await _context.MeetingAgendaRelations.Where(r => r.MeetingMinutesId == meeting.Id)
            .Select(r => r.MeetingAgendaId).ToListAsync()).ToHashSet();
        foreach (var agenda in agendas)
        {
            agenda.Status = AgendaStatus.Archived;
            agenda.ArchivedAt = AppTime.Now;
            agenda.ArchivedByMeetingId = meeting.Id;
            agenda.LastModifiedAt = AppTime.Now;
            if (relatedIds.Add(agenda.Id))
                _context.MeetingAgendaRelations.Add(new MeetingAgendaRelation
                { MeetingMinutesId = meeting.Id, MeetingAgendaId = agenda.Id, CreatedAt = AppTime.Now });
        }
    }

    public class MeetingAgendaArchiveJudgement
    {
        public int TaskId { get; set; }
        public bool Discussed { get; set; }
        public bool Rejected { get; set; }
        public bool Changed { get; set; }
    }

    private async Task<List<MeetingAgendaArchiveJudgement>?> JudgeTasksByMeetingContentAsync(
        string meetingText, List<(int TaskId, string Title)> candidateTasks)
    {
        if (string.IsNullOrWhiteSpace(meetingText) || !candidateTasks.Any())
            return null;

        try
        {
            var taskListJson = string.Join(",\n",
                candidateTasks.Select(t => $"{{\"taskId\": {t.TaskId}, \"title\": \"{t.Title.Replace("\"", "'")}\"}}"));

            var prompt = $@"你是项目会议纪要分析助手。请根据会议内容，逐个判断下列任务在本次会议中的讨论情况。

会议内容：
{meetingText}

待审核任务列表：
[
{taskListJson}
]

判断标准：
1. discussed：会议内容是否明确讨论了该任务（含同义表述、方案名称、关键词，如""复盘""""办结""""方案落地""等）
2. rejected：会议是否否定了该任务的成果或要求重做/退回（如：方案不行、打回重做、推倒重来、重新调研）
3. changed：会议是否对该任务做出了新的变更决定（修改任务内容/负责人/截止时间/优先级，或表明任务实际未完成需要继续推进）

注意：
- 会议未提及的任务：discussed=false, rejected=false, changed=false
- 只是总结汇报已完成成果、确认按计划推进：discussed=true, rejected=false, changed=false

仅返回JSON数组，不要任何其他文字：
[{{""taskId"": 1, ""discussed"": true, ""rejected"": false, ""changed"": false}}]";

            var options = new AIChatOptions
            {
                Temperature = 0.1,
                MaxTokens = 2000,
                TimeoutSeconds = 60
            };

            var response = await _aiService.GetChatCompletionAsync(prompt, options);
            if (string.IsNullOrWhiteSpace(response))
                return null;

            int start = response.IndexOf('[');
            int end = response.LastIndexOf(']');
            if (start < 0 || end <= start)
                return null;

            var json = response.Substring(start, end - start + 1);
            using var doc = JsonDocument.Parse(json);

            var validTaskIds = candidateTasks.Select(t => t.TaskId).ToHashSet();
            var result = new List<MeetingAgendaArchiveJudgement>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("taskId", out var tid))
                    continue;

                int taskId = tid.ValueKind == JsonValueKind.Number ? tid.GetInt32() : 0;
                if (taskId == 0 || !validTaskIds.Contains(taskId))
                    continue;

                if (!item.TryGetProperty("discussed", out var d) || d.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !item.TryGetProperty("rejected", out var r) || r.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !item.TryGetProperty("changed", out var c) || c.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                result.Add(new MeetingAgendaArchiveJudgement
                {
                    TaskId = taskId,
                    Discussed = d.GetBoolean(),
                    Rejected = r.GetBoolean(),
                    Changed = c.GetBoolean()
                });
            }

            return result.Any() ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTaskMentionedInMeetingText(string meetingText, string taskTitle)
    {
        if (string.IsNullOrWhiteSpace(meetingText) || string.IsNullOrWhiteSpace(taskTitle))
            return false;

        var normalizedText = NormalizeTextForMatching(meetingText);
        var normalizedTitle = NormalizeTextForMatching(taskTitle);

        if (normalizedTitle.Length < 4)
            return false;

        if (normalizedText.Contains(normalizedTitle))
            return true;

        return CalculateMatchScore(normalizedText, normalizedTitle) >= 0.5;
    }

    private async Task AddTaskChangeAuditLogAsync(
        ToDoTask task,
        string operationType,
        int operatorId,
        string? beforeStatus = null,
        string? afterStatus = null,
        int? beforeAssigneeId = null,
        int? afterAssigneeId = null,
        DateTime? beforeDeadline = null,
        DateTime? afterDeadline = null,
        string? beforePriority = null,
        string? afterPriority = null,
        int? projectId = null)
    {
        var operatorUser = await _context.Users.FindAsync(operatorId);
        var project = projectId.HasValue ? await _context.Project.FindAsync(projectId.Value) : null;

        var beforeContent = $"状态: {beforeStatus ?? "未设置"}；负责人ID: {beforeAssigneeId?.ToString() ?? "未设置"}；截止时间: {beforeDeadline?.ToString("yyyy-MM-dd") ?? "未设置"}；优先级: {beforePriority ?? "未设置"}";
        var afterContent = $"状态: {afterStatus ?? "未设置"}；负责人ID: {afterAssigneeId?.ToString() ?? "未设置"}；截止时间: {afterDeadline?.ToString("yyyy-MM-dd") ?? "未设置"}；优先级: {afterPriority ?? "未设置"}";

        var log = new ChangeLog
        {
            OperationType = operationType == "创建" ? OperationType.创建 : OperationType.更新,
            OperationStatus = OperationStatus.成功,
            OperatedAt = AppTime.Now,
            OperationTarget = OperationTarget.任务,
            OperatedByUserId = operatorId,
            OperatedByUserName = operatorUser?.UserName ?? "",
            OperatedByRealName = operatorUser?.RealName ?? operatorUser?.UserName ?? "",
            ProjectId = projectId,
            ProjectName = project?.Name,
            TargetId = task.Id,
            TargetName = task.Title,
            TaskId = task.Id,
            TaskName = task.Title,
            BeforeContent = beforeContent,
            AfterContent = afterContent
        };

        _context.ChangeLogs.Add(log);
        await _context.SaveChangesAsync();
    }

    private static ToDoTask? FindMatchingTask(
       List<ToDoTask> projectTasks,
       string taskTitle,
       string taskDescription,
       string fullTaskContent)
    {
        if (projectTasks == null || !projectTasks.Any()) return null;

        var normalizedTaskTitle = NormalizeTaskText(taskTitle);
        var normalizedTaskDesc = NormalizeTaskText(taskDescription ?? string.Empty);
        var normalizedFullContent = NormalizeTaskText(fullTaskContent);
        if (string.IsNullOrWhiteSpace(normalizedTaskTitle)) return null;

        var exactMatch = projectTasks.FirstOrDefault(task =>
            string.Equals(NormalizeTaskText(task.Title), normalizedTaskTitle, StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null) return exactMatch;

        var containsMatch = projectTasks.FirstOrDefault(task =>
        {
            var normalizedTask = NormalizeTaskText(task.Title);
            var shorterLength = Math.Min(normalizedTask.Length, normalizedTaskTitle.Length);
            var longerLength = Math.Max(normalizedTask.Length, normalizedTaskTitle.Length);
            return shorterLength >= 6
                && longerLength > 0
                && (double)shorterLength / longerLength >= 0.75
                && (normalizedTask.Contains(normalizedTaskTitle) || normalizedTaskTitle.Contains(normalizedTask));
        });
        if (containsMatch != null) return containsMatch;

        var descContainsTask = projectTasks.FirstOrDefault(task =>
        {
            var normalizedTask = NormalizeTaskText(task.Title);
            return normalizedTask.Length >= 6
                && (normalizedTaskDesc.Contains(normalizedTask) || normalizedFullContent.Contains(normalizedTask));
        });
        if (descContainsTask != null) return descContainsTask;

        var keywordMatch = projectTasks
            .Select(task => new
            {
                Task = task,
                Score = CalculateKeywordMatchScore(normalizedTaskTitle, NormalizeTaskText(task.Title))
            })
            .OrderByDescending(x => x.Score)
            .FirstOrDefault(x => x.Score >= 0.75);
        if (keywordMatch != null) return keywordMatch.Task;

        var descSimilarityMatch = projectTasks
            .Select(task => new
            {
                Task = task,
                Score = CalculateDescriptionSimilarity(normalizedTaskDesc, NormalizeTaskText(task.Description ?? string.Empty))
            })
            .OrderByDescending(x => x.Score)
            .FirstOrDefault(x => x.Score >= 0.65);
        if (descSimilarityMatch != null) return descSimilarityMatch.Task;

        return null;
    }

    private static double CalculateDescriptionSimilarity(string text1, string text2)
    {
        if (string.IsNullOrEmpty(text1) || string.IsNullOrEmpty(text2)) return 0;

        var keywords1 = GetCharacterNgrams(text1, 2);
        var keywords2 = GetCharacterNgrams(text2, 2);

        if (!keywords1.Any() || !keywords2.Any()) return 0;

        var commonKeywords = keywords1.Intersect(keywords2).Count();
        var maxCount = Math.Max(keywords1.Count, keywords2.Count);

        return maxCount > 0 ? (double)commonKeywords / maxCount : 0;
    }

    private static double CalculateKeywordMatchScore(string text1, string text2)
    {
        if (string.IsNullOrEmpty(text1) || string.IsNullOrEmpty(text2)) return 0;

        var keywords1 = GetCharacterNgrams(text1, 2);
        var keywords2 = GetCharacterNgrams(text2, 2);

        if (!keywords1.Any() || !keywords2.Any()) return 0;

        var commonKeywords = keywords1.Intersect(keywords2).Count();
        var maxCount = Math.Max(keywords1.Count, keywords2.Count);

        return maxCount > 0 ? (double)commonKeywords / maxCount : 0;
    }

    private static HashSet<string> GetCharacterNgrams(string text, int n)
    {
        var result = new HashSet<string>();
        if (string.IsNullOrEmpty(text) || text.Length < n) return result;

        for (int i = 0; i <= text.Length - n; i++)
        {
            result.Add(text.Substring(i, n));
        }
        return result;
    }

    private static bool HasExplicitStatusChange(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var markers = new[]
        {
            "状态", "标记为完成", "改为已完成", "进入进行中", "改为进行中",
            "重新开始", "恢复任务", "取消任务", "待审核",
            "NotStarted", "InProgress", "Completed", "Cancelled", "PendingConfirmation"
        };
        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasExplicitPriorityChange(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var markers = new[]
        {
            "优先级", "高优", "中优", "低优", "High", "Medium", "Low"
        };
        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static ApplicationUser? FindAssigneeForTodo(IEnumerable<ApplicationUser> members, MeetingTaskParseItem todo, int? leaderUserId)
    {
        if (!string.IsNullOrWhiteSpace(todo.AssigneeName))
        {
            if (todo.AssigneeName is ("负责人" or "项目负责人" or "项目leader" or "leader" or "项目经理" or "负责人安排"))
            {
                var leader = FindLeaderOrAdmin(members, leaderUserId);
                if (leader != null) return leader;
            }

            var matched = FindAssignee(members, todo.AssigneeName);
            if (matched != null) return matched;
        }

        var textToSearch = $"{todo.Title} {todo.Description}";
        if (!string.IsNullOrWhiteSpace(textToSearch))
        {
            foreach (var member in members.OrderByDescending(m => (m.RealName?.Length ?? 0)))
            {
                if (!string.IsNullOrWhiteSpace(member.RealName) && textToSearch.Contains(member.RealName, StringComparison.OrdinalIgnoreCase))
                    return member;
                if (!string.IsNullOrWhiteSpace(member.UserName) && textToSearch.Contains(member.UserName, StringComparison.OrdinalIgnoreCase))
                    return member;
            }

            if (textToSearch.Contains("负责人"))
            {
                var leader = FindLeaderOrAdmin(members, leaderUserId);
                if (leader != null) return leader;
            }
        }

        return null;
    }

    private static ApplicationUser? FindLeaderOrAdmin(IEnumerable<ApplicationUser> members, int? leaderUserId)
    {
        if (leaderUserId.HasValue && leaderUserId.Value > 0)
        {
            var leader = members.FirstOrDefault(m => m.Id == leaderUserId.Value);
            if (leader != null) return leader;
        }

        return members.FirstOrDefault();
    }

    public static bool IsQuotePresentInSource(string sourceText, string quote)
    {
        return MeetingQuoteEvidence.TryResolveFromSource(sourceText, quote, out _);
    }

    public static string CorrectNamesInText(string text, IEnumerable<ApplicationUser> members, int? leaderUserId)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var result = text;

        var leader = FindLeaderOrAdmin(members, leaderUserId);
        if (leader != null && !string.IsNullOrWhiteSpace(leader.RealName))
        {
            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"(负责人|项目负责人|项目leader|leader|项目经理|项目Leader)",
                leader.RealName);
        }

        foreach (var member in members)
        {
            if (string.IsNullOrWhiteSpace(member.RealName)) continue;
            var realName = member.RealName;

            if (result.Contains(realName, StringComparison.OrdinalIgnoreCase))
                continue;

            var nameVariants = GenerateNameVariants(realName);
            foreach (var variant in nameVariants)
            {
                if (variant == realName) continue;
                if (result.Contains(variant, StringComparison.Ordinal))
                {
                    result = result.Replace(variant, realName);
                    break;
                }
            }
        }

        return result;
    }

    private static string RemoveTodoSection(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var result = text;

        result = System.Text.RegularExpressions.Regex.Replace(result,
            @"待办事项[：:].*?(?=\n|$)",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);

        result = System.Text.RegularExpressions.Regex.Replace(result,
            @"待办事项[：:]\s*[\s\S]*?(?=\n\s*\n|会议概览|核心讨论|会议结论|$)",
            string.Empty);

        result = System.Text.RegularExpressions.Regex.Replace(result, @"\n{3,}", "\n\n");
        result = result.Trim();

        return result;
    }

    private static IEnumerable<string> GenerateNameVariants(string realName)
    {
        var variants = new List<string>();

        if (realName.Length == 3)
        {
            for (int i = 1; i < 3; i++)
            {
                var chars = realName.ToCharArray();
                chars[i] = GetCommonHomophone(chars[i]);
                variants.Add(new string(chars));
            }

            var chars2 = realName.ToCharArray();
            chars2[1] = GetCommonHomophone(chars2[1]);
            variants.Add(new string(chars2));
        }
        else if (realName.Length == 2)
        {
            var chars = realName.ToCharArray();
            chars[1] = GetCommonHomophone(chars[1]);
            variants.Add(new string(chars));
        }

        return variants.Distinct();
    }

    private static char GetCommonHomophone(char c)
    {
        return c switch
        {
            '薇' => '为',
            '为' => '薇',
            '贺' => '和',
            '和' => '贺',
            '辄' => '哲',
            '哲' => '辄',
            '明' => '名',
            '名' => '明',
            '华' => '花',
            '花' => '华',
            '珂' => '课',
            '课' => '珂',
            '强' => '墙',
            '墙' => '强',
            '磊' => '雷',
            '雷' => '磊',
            '军' => '均',
            '均' => '军',
            '涛' => '滔',
            '楚' => '出',
            '出' => '楚',
            '滔' => '涛',
            '超' => '朝',
            '朝' => '超',
            '飞' => '非',
            '非' => '飞',
            '鹏' => '朋',
            '朋' => '鹏',
            '峰' => '风',
            '风' => '峰',
            '锋' => '风',
            '亮' => '量',
            '量' => '亮',
            '辉' => '晖',
            '晖' => '辉',
            '博' => '搏',
            '搏' => '博',
            '文' => '闻',
            '闻' => '文',
            '武' => '舞',
            '舞' => '武',
            '静' => '敬',
            '敬' => '静',
            '丽' => '利',
            '利' => '丽',
            '娜' => '那',
            '那' => '娜',
            '敏' => '明',
            '雪' => '血',
            '芳' => '方',
            '方' => '芳',
            '莉' => '利',
            '燕' => '烟',
            '烟' => '燕',
            '玲' => '灵',
            '灵' => '玲',
            '珍' => '真',
            '真' => '珍',
            '茹' => '如',
            '如' => '茹',
            '婷' => '亭',
            '亭' => '婷',
            '欣' => '新',
            '新' => '欣',
            '怡' => '宜',
            '宜' => '怡',
            '琳' => '林',
            '林' => '琳',
            '瑶' => '遥',
            '遥' => '瑶',
            '萱' => '宣',
            '宣' => '萱',
            '妍' => '研',
            '研' => '妍',
            '菲' => '非',
            '蕾' => '雷',
            '瑾' => '谨',
            '谨' => '瑾',
            '瑄' => '宣',
            '珺' => '君',
            '君' => '珺',
            '琰' => '炎',
            '炎' => '琰',
            '钰' => '玉',
            '玉' => '钰',
            '铭' => '明',
            '锐' => '睿',
            '睿' => '锐',
            '锦' => '金',
            '金' => '锦',
            '祺' => '齐',
            '齐' => '祺',
            '轩' => '干',
            '涵' => '含',
            '含' => '涵',
            '晗' => '含',
            '暄' => '宣',
            '澈' => '撤',
            '撤' => '澈',
            '东' => '冬',
            '冬' => '东',
            '煜' => '宇',
            '宇' => '雨',
            '雨' => '宇',
            '炜' => '伟',
            '伟' => '炜',
            '煊' => '宣',
            '焱' => '炎',
            '淼' => '秒',
            '秒' => '淼',
            '皓' => '浩',
            '浩' => '皓',
            '昊' => '浩',
            '宸' => '辰',
            '辰' => '宸',
            '晨' => '辰',
            '泽' => '择',
            '择' => '泽',
            '朗' => '浪',
            '浪' => '朗',
            '旭' => '九',
            '九' => '旭',
            '昂' => '仰',
            '仰' => '昂',
            '曦' => '希',
            '希' => '曦',
            '曜' => '尧',
            '尧' => '曜',
            _ => c
        };
    }

    private static ApplicationUser? FindAssignee(IEnumerable<ApplicationUser> members, string? assignee)
    {
        if (string.IsNullOrWhiteSpace(assignee)) return null;

        var normalizedAssignee = NormalizeName(assignee);

        var exactMatch = members.FirstOrDefault(user =>
            string.Equals(user.RealName, assignee, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(user.UserName, assignee, StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null) return exactMatch;

        var containsMatch = members.FirstOrDefault(user =>
            (user.RealName?.Contains(assignee, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (user.UserName?.Contains(assignee, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (user.RealName != null && assignee.Contains(user.RealName, StringComparison.OrdinalIgnoreCase)) ||
            (user.UserName != null && assignee.Contains(user.UserName, StringComparison.OrdinalIgnoreCase)));
        if (containsMatch != null) return containsMatch;

        var normalizedMatch = members.FirstOrDefault(user =>
            (user.RealName != null && NormalizeName(user.RealName).Contains(normalizedAssignee)) ||
            (user.UserName != null && NormalizeName(user.UserName).Contains(normalizedAssignee)) ||
            (normalizedAssignee.Contains(user.RealName != null ? NormalizeName(user.RealName) : "")) ||
            (normalizedAssignee.Contains(user.UserName != null ? NormalizeName(user.UserName) : "")));
        if (normalizedMatch != null) return normalizedMatch;

        var typoMatch = members
            .Select(user => new
            {
                User = user,
                RealNameScore = GetTypoToleranceScore(normalizedAssignee, user.RealName),
                UserNameScore = GetTypoToleranceScore(normalizedAssignee, user.UserName),
                LcsScore = GetNameSimilarity(assignee, user.RealName)
            })
            .Select(x => new { x.User, Score = Math.Max(Math.Max(x.RealNameScore, x.UserNameScore), x.LcsScore) })
            .OrderByDescending(x => x.Score)
            .FirstOrDefault(x => x.Score >= 0.6);
        if (typoMatch != null) return typoMatch.User;

        return null;
    }

    private static string NormalizeName(string name)
    {
        return string.Concat(name.Trim().Where(c => !char.IsWhiteSpace(c) && c != '·' && c != '.' && c != '·'));
    }

    private static double GetTypoToleranceScore(string source, string? target)
    {
        if (string.IsNullOrEmpty(target)) return 0;
        var targetNorm = NormalizeName(target);
        if (source.Length == 0 || targetNorm.Length == 0) return 0;

        if (source.Length == targetNorm.Length)
        {
            int diffCount = 0;
            int sameCount = 0;
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == targetNorm[i]) sameCount++;
                else diffCount++;
            }

            int maxAllowedDiff = source.Length switch
            {
                2 => 0,
                3 => 2,
                _ => 2
            };

            if (diffCount <= maxAllowedDiff && sameCount >= 1)
                return 0.9;
        }

        if (Math.Abs(source.Length - targetNorm.Length) == 1)
        {
            var longer = source.Length > targetNorm.Length ? source : targetNorm;
            var shorter = source.Length > targetNorm.Length ? targetNorm : source;
            int matchCount = 0;
            int si = 0;
            for (int i = 0; i < longer.Length && si < shorter.Length; i++)
            {
                if (longer[i] == shorter[si])
                {
                    matchCount++;
                    si++;
                }
            }
            if (matchCount == shorter.Length)
                return 0.85;
        }

        return 0;
    }

    private static double GetNameSimilarity(string source, string? target)
    {
        if (string.IsNullOrEmpty(target)) return 0;
        var sourceNorm = NormalizeName(source);
        var targetNorm = NormalizeName(target);
        if (sourceNorm.Length == 0 || targetNorm.Length == 0) return 0;

        if (targetNorm.Contains(sourceNorm) || sourceNorm.Contains(targetNorm))
            return 0.85;

        int lcsLen = LcsLength(sourceNorm, targetNorm);
        return (double)lcsLen / Math.Max(sourceNorm.Length, targetNorm.Length);
    }

    private static int LcsLength(string a, string b)
    {
        int[,] dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                dp[i, j] = a[i - 1] == b[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        return dp[a.Length, b.Length];
    }

    private static List<AIMeetingTodo> FallbackParse(string content)
    {
        return content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimStart('-', '*', '•', '1', '2', '3', '4', '5', '.', '、', ' '))
            .Where(line => line.Contains("待办", StringComparison.OrdinalIgnoreCase) || line.Contains("负责", StringComparison.OrdinalIgnoreCase))
            .Select(line => new AIMeetingTodo { Content = line.Replace("待办：", "").Replace("待办:", "").Trim() })
            .ToList();
    }

    private static string BuildFallbackSummary(string content)
    {
        var summary = content.Trim();
        return summary.Length <= 500 ? summary : summary[..500] + "...";
    }

    private static string NormalizeTaskText(string value)
    {
        return string.Concat(value.Trim().Where(character => !char.IsWhiteSpace(character)))
            .Trim('。', '.', '!', '！', '?', '？', ':', '：', ';', '；');
    }

    private async Task<bool> CanAccessMeetingAsync(MeetingMinutes meeting, ApplicationUser user)
    {
        if (user.Role == UserRole.systemAdmin) return true;
        if (meeting.Project?.LeaderUserId == user.Id) return true;
        var membership = await _context.ProjectUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.ProjectId == meeting.ProjectId && item.UserId == user.Id);
        return membership != null
            && (meeting.Project?.IsEncrypted != '2' || membership.ProjectRole == (int)ProjectRole.Admin);
    }

    public async Task<bool> CanApproveWritesAsync(MeetingMinutes meeting, ApplicationUser user)
    {
        if (user.Role == UserRole.systemAdmin) return true;
        if (meeting.Project?.LeaderUserId == user.Id) return true;
        return await _context.ProjectUsers
            .AsNoTracking()
            .AnyAsync(item => item.ProjectId == meeting.ProjectId
                && item.UserId == user.Id
                && item.ProjectRole == (int)ProjectRole.Admin);
    }

    private async Task<List<int>> GetApproverIdsAsync(MeetingMinutes meeting)
    {
        var ids = await _context.ProjectUsers
            .AsNoTracking()
            .Where(item => item.ProjectId == meeting.ProjectId && item.ProjectRole == (int)ProjectRole.Admin)
            .Select(item => item.UserId)
            .ToListAsync();
        ids.AddRange(await _context.Users
            .AsNoTracking()
            .Where(item => item.Role == UserRole.systemAdmin && !item.IsDeleted)
            .Select(item => item.Id)
            .ToListAsync());
        if (meeting.Project?.LeaderUserId > 0) ids.Add(meeting.Project.LeaderUserId);
        return ids.Distinct().ToList();
    }

    private static List<MeetingTaskParseItem> RuleBasedFallbackParse(string content)
    {
        var items = new List<MeetingTaskParseItem>();
        if (string.IsNullOrWhiteSpace(content))
            return items;

        var now = AppTime.Now;
        var sentences = content.Split(new[] { '，', '。', '；', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var sentence in sentences)
        {
            var trimmed = sentence.Trim();
            if (trimmed.Length < 5) continue;

            var hasActionVerb = trimmed.Contains("完成") || trimmed.Contains("提供") || trimmed.Contains("输出")
                              || trimmed.Contains("落地") || trimmed.Contains("安排") || trimmed.Contains("负责")
                              || trimmed.Contains("明确") || trimmed.Contains("同步") || trimmed.Contains("研讨")
                              || trimmed.Contains("实现") || trimmed.Contains("评估") || trimmed.Contains("修改")
                              || trimmed.Contains("修复") || trimmed.Contains("开发") || trimmed.Contains("提交")
                              || trimmed.Contains("发布") || trimmed.Contains("准备");

            var hasTimeWord = trimmed.Contains("今天") || trimmed.Contains("明天") || trimmed.Contains("后天")
                            || trimmed.Contains("本周") || trimmed.Contains("下周") || trimmed.Contains("月底")
                            || trimmed.Contains("月初") || trimmed.Contains("季度") || trimmed.Contains("天内")
                            || trimmed.Contains("日前") || trimmed.Contains("日期")
                            || System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"\d{1,2}月\d{1,2}日")
                            || System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"\d{1,2}号");

            var hasPersonWord = trimmed.Contains("负责") || trimmed.Contains("明确") || trimmed.Contains("安排")
                              || System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"[\u4e00-\u9fa5]{2,4}[：:]");

            if (!hasActionVerb) continue;
            if (!hasTimeWord && !hasPersonWord) continue;
            if (trimmed.Contains("暂不") || trimmed.Contains("不展开") || trimmed.Contains("讨论"))
                continue;

            var title = trimmed.Length > 100 ? trimmed[..100] + "..." : trimmed;

            var deadlineStr = ExtractDeadline(trimmed, now);

            string? assignee = null;
            var assigneeMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"([\u4e00-\u9fa5]{2,4})(?:明确|负责|安排|要求)");
            if (assigneeMatch.Success)
            {
                var extracted = assigneeMatch.Groups[1].Value;
                if (extracted is not ("负责人" or "管理员" or "经理" or "主管" or "领导" or "产品" or "前端" or "后端" or "测试" or "运维" or "开发" or "团队" or "项目"))
                    assignee = extracted;
            }

            string? group = null;
            if (trimmed.Contains("前端")) group = "前端组";
            else if (trimmed.Contains("后端")) group = "后端组";
            else if (trimmed.Contains("测试")) group = "测试组";
            else if (trimmed.Contains("运维")) group = "运维组";

            items.Add(new MeetingTaskParseItem
            {
                Title = title,
                Description = trimmed,
                Deadline = deadlineStr,
                AssigneeName = assignee,
                Priority = "Medium",
                Status = "NotStarted",
                Group = group
            });
        }

        return items;
    }

    private static string? ExtractDeadline(string text, DateTime now)
    {
        var dateMatch = System.Text.RegularExpressions.Regex.Match(text, @"(\d{1,2})月(\d{1,2})[日号]");
        if (dateMatch.Success)
        {
            var month = int.Parse(dateMatch.Groups[1].Value);
            var day = int.Parse(dateMatch.Groups[2].Value);
            try
            {
                return new DateTime(now.Year, month, day).ToString("yyyy-MM-dd");
            }
            catch { }
        }

        if (text.Contains("本周") && text.Contains("五"))
        {
            var daysUntilFriday = (int)DayOfWeek.Friday - (int)now.DayOfWeek;
            if (daysUntilFriday < 0) daysUntilFriday += 7;
            return now.AddDays(daysUntilFriday).ToString("yyyy-MM-dd");
        }
        if (text.Contains("本周") && text.Contains("三"))
        {
            var daysUntilWednesday = (int)DayOfWeek.Wednesday - (int)now.DayOfWeek;
            if (daysUntilWednesday < 0) daysUntilWednesday += 7;
            return now.AddDays(daysUntilWednesday).ToString("yyyy-MM-dd");
        }
        if (text.Contains("本周") && text.Contains("一"))
        {
            var daysUntilMonday = (int)DayOfWeek.Monday - (int)now.DayOfWeek;
            if (daysUntilMonday < 0) daysUntilMonday += 7;
            return now.AddDays(daysUntilMonday).ToString("yyyy-MM-dd");
        }

        if (text.Contains("下周一"))
        {
            var daysUntilNextMonday = (int)DayOfWeek.Monday - (int)now.DayOfWeek + 7;
            if (daysUntilNextMonday <= 7) daysUntilNextMonday += 7;
            return now.AddDays(daysUntilNextMonday).ToString("yyyy-MM-dd");
        }

        var dayNames = new Dictionary<string, DayOfWeek>
        {
            ["一"] = DayOfWeek.Monday,
            ["二"] = DayOfWeek.Tuesday,
            ["三"] = DayOfWeek.Wednesday,
            ["四"] = DayOfWeek.Thursday,
            ["五"] = DayOfWeek.Friday,
            ["六"] = DayOfWeek.Saturday,
            ["日"] = DayOfWeek.Sunday,
            ["天"] = DayOfWeek.Sunday
        };

        foreach (var kvp in dayNames)
        {
            if (text.Contains(kvp.Key) && (text.Contains("周") || text.Contains("星期")))
            {
                var targetDay = kvp.Value;
                var daysUntilTarget = (int)targetDay - (int)now.DayOfWeek;
                if (daysUntilTarget <= 0) daysUntilTarget += 7;
                return now.AddDays(daysUntilTarget).ToString("yyyy-MM-dd");
            }
        }

        var daysMatch = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)天内");
        if (daysMatch.Success)
        {
            var days = int.Parse(daysMatch.Groups[1].Value);
            return now.AddDays(days).ToString("yyyy-MM-dd");
        }

        if (text.Contains("下周"))
        {
            return now.AddDays(7).ToString("yyyy-MM-dd");
        }

        if (text.Contains("月底"))
        {
            var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
            return new DateTime(now.Year, now.Month, daysInMonth).ToString("yyyy-MM-dd");
        }

        return null;
    }

    private static int? MatchAgenda(string taskTitle, string taskDescription, List<MeetingAgenda> agendas)
    {
        if (!agendas.Any())
            return null;

        var taskText = $"{taskTitle} {taskDescription}";
        var normalizedTaskText = NormalizeTextForMatching(taskText);

        var scoredAgendas = agendas
            .Select(agenda => new
            {
                Agenda = agenda,
                Score = CalculateMatchScore(normalizedTaskText, NormalizeTextForMatching(agenda.Title))
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        if (scoredAgendas.Any() && scoredAgendas[0].Score >= 0.15)
        {
            return scoredAgendas[0].Agenda.Id;
        }

        return null;
    }

    private static string NormalizeTextForMatching(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return text.ToLowerInvariant()
            .Replace(" ", "").Replace("，", "").Replace("。", "").Replace("、", "")
            .Replace("：", "").Replace(":", "").Replace("；", "").Replace(";", "")
            .Replace("！", "").Replace("!", "").Replace("？", "").Replace("?", "")
            .Replace(",", "").Replace(".", "")
            .Replace("（", "").Replace("）", "").Replace("(", "").Replace(")", "")
            .Trim();
    }

    private static double CalculateMatchScore(string taskText, string agendaText)
    {
        if (string.IsNullOrEmpty(taskText) || string.IsNullOrEmpty(agendaText))
            return 0;

        double score = 0;

        if (taskText.Contains(agendaText))
            score += 1.0;

        if (agendaText.Contains(taskText))
            score += 0.8;

        var taskBigrams = GetCharacterNgrams(taskText, 2);
        var agendaBigrams = GetCharacterNgrams(agendaText, 2);

        if (taskBigrams.Any() && agendaBigrams.Any())
        {
            var commonBigrams = taskBigrams.Intersect(agendaBigrams).Count();
            var totalUniqueBigrams = taskBigrams.Union(agendaBigrams).Count();
            if (totalUniqueBigrams > 0)
            {
                score += 0.4 * (double)commonBigrams / totalUniqueBigrams;
            }
        }

        var taskTrigrams = GetCharacterNgrams(taskText, 3);
        var agendaTrigrams = GetCharacterNgrams(agendaText, 3);

        if (taskTrigrams.Any() && agendaTrigrams.Any())
        {
            var commonTrigrams = taskTrigrams.Intersect(agendaTrigrams).Count();
            var totalUniqueTrigrams = taskTrigrams.Union(agendaTrigrams).Count();
            if (totalUniqueTrigrams > 0)
            {
                score += 0.6 * (double)commonTrigrams / totalUniqueTrigrams;
            }
        }

        var lcsScore = CalculateLCSScore(taskText, agendaText);
        score += 0.5 * lcsScore;

        return score;
    }

    private static double CalculateLCSScore(string text1, string text2)
    {
        if (string.IsNullOrEmpty(text1) || string.IsNullOrEmpty(text2))
            return 0;

        int[,] matrix = new int[text1.Length + 1, text2.Length + 1];

        for (int i = 1; i <= text1.Length; i++)
        {
            for (int j = 1; j <= text2.Length; j++)
            {
                if (text1[i - 1] == text2[j - 1])
                    matrix[i, j] = matrix[i - 1, j - 1] + 1;
                else
                    matrix[i, j] = Math.Max(matrix[i - 1, j], matrix[i, j - 1]);
            }
        }

        int lcsLength = matrix[text1.Length, text2.Length];
        int maxLength = Math.Max(text1.Length, text2.Length);

        return maxLength > 0 ? (double)lcsLength / maxLength : 0;
    }

    private async Task<int?> GetTaskGroupIdByNameAsync(int projectId, string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            return null;

        return await _context.TaskGroups
            .Where(g => g.ProjectId == projectId
                     && !g.IsDeleted
                     && g.Name == groupName.Trim())
            .Select(g => (int?)g.Id)
            .FirstOrDefaultAsync();
    }
}

public sealed class MeetingSyncResult
{
    public bool Success { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;
    public List<MeetingActionItem> Items { get; init; } = new();

    public static MeetingSyncResult Successful(string summary, List<MeetingActionItem> items) => new()
    {
        Success = true,
        Summary = summary,
        Items = items
    };

    public static MeetingSyncResult Failed(string message) => new() { ErrorMessage = message };
}

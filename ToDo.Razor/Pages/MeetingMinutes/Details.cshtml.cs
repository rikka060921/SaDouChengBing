using Markdig;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;

namespace ToDo.Razor.Pages.MeetingMinutes;

public class DetailsModel : PageModel
{
    private readonly IMeetingMinutesService _service;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly MeetingTaskSyncService _syncService;
    private readonly MeetingActionSupervisionService _supervisionService;
    private readonly ApplicationDbContext _context;
    private readonly ToDoTaskDomainService _taskDomainService;
    private readonly IAIService _aiService;
    private readonly ILogger<DetailsModel> _logger;

    public DetailsModel(
        IMeetingMinutesService service,
        UserManager<ApplicationUser> userManager,
        MeetingTaskSyncService syncService,
        MeetingActionSupervisionService supervisionService,
        ApplicationDbContext context,
        ToDoTaskDomainService taskDomainService,
        IAIService aiService,
        ILogger<DetailsModel> logger)
    {
        _service = service;
        _userManager = userManager;
        _syncService = syncService;
        _supervisionService = supervisionService;
        _context = context;
        _taskDomainService = taskDomainService;
        _aiService = aiService;
        _logger = logger;
    }
    public async Task<IActionResult> OnPostUpdateActionItemProjectAsync(int itemId, int meetingId, int? newProjectId)
    {
        try
        {
            var meeting = await GetMeetingForActionItemWriteAsync(meetingId);
            if (meeting == null)
                return new JsonResult(new { success = false, msg = "你没有权限修改该会议的任务" })
                { StatusCode = StatusCodes.Status403Forbidden };

            var item = await _context.MeetingActionItems
                .FirstOrDefaultAsync(x => x.Id == itemId && x.MeetingMinutesId == meetingId);
            if (item == null)
                return new JsonResult(new { success = false, msg = "未找到该行动项" });

            // 校验新项目必须在会议关联项目里
            var validIds = await _context.MeetingMinutesProjects
                .Where(x => x.MeetingMinutesId == meetingId)
                .Select(x => x.ProjectId)
                .ToListAsync();
            if (validIds.Count == 0) validIds.Add(meeting.ProjectId);

            if (newProjectId.HasValue && !validIds.Contains(newProjectId.Value))
                return new JsonResult(new { success = false, msg = "项目不在本次会议关联范围内" });

            item.ProjectId = newProjectId;
            var effectiveProjectId = newProjectId ?? meeting.ProjectId;

            // 清掉不属于新项目的数据
            if (item.MatchedTaskId.HasValue)
            {
                var taskOk = await _context.ToDoTasks.AnyAsync(t =>
                    t.Id == item.MatchedTaskId.Value
                    && t.ProjectId == effectiveProjectId
                    && !t.IsDeleted);
                if (!taskOk) item.MatchedTaskId = null;
            }

            if (item.AssigneeId.HasValue)
            {
                var ok = await _context.ProjectUsers
                    .AnyAsync(pu => pu.ProjectId == effectiveProjectId && pu.UserId == item.AssigneeId.Value);
                if (!ok) item.AssigneeId = null;
            }

            if (!string.IsNullOrEmpty(item.GroupName))
            {
                var ok = await _context.TaskGroups.AnyAsync(g =>
                    g.ProjectId == effectiveProjectId && !g.IsDeleted && g.Name == item.GroupName);
                if (!ok) item.GroupName = null;
            }

            await _context.SaveChangesAsync();
            return new JsonResult(new { success = true, msg = "项目已更新" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update action item project {ItemId}", itemId);
            return new JsonResult(new { success = false, msg = "保存失败，请稍后重试" });
        }
    }
    // ====================== 行内字段更新（只返回JSON） ======================
    public async Task<IActionResult> OnPostUpdateActionItemFieldAsync(
        int itemId,
        int meetingId,
        string? assigneeId = null,
        string? deadline = null,
        string? priority = null,
        string? taskStatus = null,
        string? groupName = null,
        int? meetingAgendaId = null,
        int? sourceDecisionIndex = null,
        int? matchedTaskId = null,
        int? prepDraftTaskId = null)
    {
        try
        {
            if (itemId <= 0 || meetingId <= 0)
            {
                return new JsonResult(new { success = false, msg = "参数ID无效" });
            }

            var meeting = await GetMeetingForActionItemWriteAsync(meetingId);
            if (meeting == null)
            {
                return new JsonResult(new { success = false, msg = "你没有权限修改该会议的任务" })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
            }

            // 查找
            var item = await _context.MeetingActionItems
                .FirstOrDefaultAsync(x => x.Id == itemId && x.MeetingMinutesId == meetingId);

            if (item == null)
            {
                return new JsonResult(new { success = false, msg = "未找到该任务或不属于当前会议" });
            }

            // ==========【新增】计算有效项目ID ==========
            var effectiveProjectId = item.ProjectId ?? meeting.ProjectId;

            // 保存原有的匹配任务ID
            var originalMatchedTaskId = item.MatchedTaskId;

            // 更新字段
            if (assigneeId != null)
            {
                // ✅ 替换：meeting.ProjectId → effectiveProjectId
                if (!await TryResolveAssigneeAsync(effectiveProjectId, assigneeId, meeting.Project?.LeaderUserId, item))
                    return new JsonResult(new { success = false, msg = "责任人必须是当前项目成员" });
            }
            if (deadline != null)
            {
                if (!TryParseDeadline(deadline, out var parsedDeadline))
                    return new JsonResult(new { success = false, msg = "截止日期格式无效" });
                item.Deadline = parsedDeadline;
            }
            if (priority != null)
            {
                if (!ValidPriorities.Contains(priority))
                    return new JsonResult(new { success = false, msg = "优先级无效" });
                item.Priority = priority;
            }
            if (taskStatus != null)
            {
                if (!ValidTaskStatuses.Contains(taskStatus))
                    return new JsonResult(new { success = false, msg = "任务状态无效" });
                item.TaskStatus = taskStatus;
            }
            if (groupName != null)
            {
                // ✅ 替换：meeting.ProjectId → effectiveProjectId
                if (!await IsValidGroupAsync(effectiveProjectId, groupName))
                    return new JsonResult(new { success = false, msg = "任务分组不属于当前项目" });
                item.GroupName = string.IsNullOrEmpty(groupName) ? null : groupName;
            }
            if (meetingAgendaId.HasValue)
            {
                if (meetingAgendaId.Value == 0)
                {
                    // 选择了「无关联」
                    item.MeetingAgendaId = null;
                }
                else
                {
                    // ✅ 替换：meeting.ProjectId → effectiveProjectId
                    var agenda = await _context.MeetingAgendas
                        .FirstOrDefaultAsync(a => a.Id == meetingAgendaId.Value && a.ProjectId == effectiveProjectId && !a.IsDeleted);
                    if (agenda == null)
                        return new JsonResult(new { success = false, msg = "关联的议题不存在" });
                    item.MeetingAgendaId = meetingAgendaId.Value;
                }
            }
            if (matchedTaskId.HasValue)
            {
                if (matchedTaskId.Value == 0)
                {
                    // 选择了「未关联」——仅清掉关联，不影响已确认的任务
                    item.MatchedTaskId = null;
                }
                else
                {
                    // ✅ 替换：meeting.ProjectId → effectiveProjectId
                    var taskEntity = await _context.ToDoTasks
                        .FirstOrDefaultAsync(t => t.Id == matchedTaskId.Value && t.ProjectId == effectiveProjectId && !t.IsDeleted);
                    if (taskEntity == null)
                        return new JsonResult(new { success = false, msg = "关联的任务不存在" });
                    item.MatchedTaskId = matchedTaskId.Value;
                }
            }
            if (prepDraftTaskId.HasValue)
            {
                // prepDraftTaskId 是草稿 JSON 快照里的 ID，不是数据库主键，只需 > 0 合法
                item.PrepDraftTaskId = prepDraftTaskId.Value == 0 ? null : prepDraftTaskId.Value;
            }
            if (sourceDecisionIndex.HasValue)
            {
                if (sourceDecisionIndex.Value <= 0)
                {
                    // 选择了「无来源决策」
                    item.SourceDecisionIndex = null;
                    item.SourceDecisionContent = null;
                }
                else
                {
                    var decisions = DeserializeDecisions(meeting.AiDecisionsJson);
                    if (decisions == null
                        || sourceDecisionIndex.Value < 1
                        || sourceDecisionIndex.Value > decisions.Count)
                    {
                        return new JsonResult(new { success = false, msg = "来源决策不存在，请重新解析任务" });
                    }
                    var decisionContent = decisions[sourceDecisionIndex.Value - 1].Content;
                    item.SourceDecisionIndex = sourceDecisionIndex.Value;
                    item.SourceDecisionContent = string.IsNullOrWhiteSpace(decisionContent)
                        ? null
                        : (decisionContent.Length > 500 ? decisionContent[..500] : decisionContent);
                }
            }

            if (item.ActionType == "TaskChange")
            {
                if (assigneeId != null) item.AfterAssigneeId = item.AssigneeId;
                if (deadline != null) item.AfterDeadline = item.Deadline;
                if (priority != null) item.AfterPriority = item.Priority;
                if (taskStatus != null) item.AfterStatus = item.TaskStatus;
            }

            // 如果已同步，同步更新对应的正式任务
            if (item.IsConfirmed && originalMatchedTaskId.HasValue)
            {
                // ✅ 替换：meeting.ProjectId → effectiveProjectId
                var updated = await UpdateMatchedTaskAsync(
                    originalMatchedTaskId.Value,
                    effectiveProjectId,
                    assigneeId,
                    deadline,
                    priority,
                    taskStatus,
                    groupName);
                if (!updated)
                {
                    return new JsonResult(new { success = false, msg = "关联的正式任务不存在、已删除或不属于当前项目" })
                    {
                        StatusCode = StatusCodes.Status409Conflict
                    };
                }
            }

            // 行动项与正式任务在同一次 SaveChanges 中原子提交。
            await _context.SaveChangesAsync();

            return new JsonResult(new { success = true, msg = "保存成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update meeting action item {ItemId} in meeting {MeetingId}", itemId, meetingId);
            return new JsonResult(new { success = false, msg = "保存失败，请稍后重试" });
        }
    }


    // ====================== 手动修改单条决策 ======================
    public async Task<JsonResult> OnPostUpdateDecisionAsync(int meetingId, int decisionIndex, string content, string decisionMaker)
    {
        try
        {
            var meeting = await GetMeetingForActionItemWriteAsync(meetingId);
            if (meeting == null)
                return new JsonResult(new { success = false, msg = "无权限修改该会议" }) { StatusCode = StatusCodes.Status403Forbidden };

            if (meeting.ConfirmedAt.HasValue)
                return new JsonResult(new { success = false, msg = "会议已确认写入任务，不可再修改决策" });

            var decisions = DeserializeDecisions(meeting.AiDecisionsJson);
            if (decisionIndex < 0 || decisionIndex >= decisions.Count)
                return new JsonResult(new { success = false, msg = "决策序号无效" });

            content = content?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(content))
                return new JsonResult(new { success = false, msg = "决策内容不能为空" });

            var originalContent = decisions[decisionIndex].Content?.Trim() ?? string.Empty;
            decisions[decisionIndex].Content = content;
            decisions[decisionIndex].DecisionMaker = (decisionMaker ?? string.Empty).Trim();

            // 重要：这里绝对不要自动重匹配/覆盖原话。
            // AI 原始标注的 OriginalQuotes / KeyQuote 是从会议转写里挖出的**完整讨论链**
            // （谁先提、谁附和、分歧、结论），这是最可靠的证据。
            // 用户手动改决策总结（Content）是改的"怎么概括这条决策"，
            // 不是改"这条决策对应的讨论过程"。两者是独立字段，互不影响。
            // 如果用户真的想让原话匹配新总结，应该用独立的"AI 重新匹配原话"按钮，
            // 那个按钮会调用 DeepSeek 重新从长转写里挖完整讨论链。

            meeting.AiDecisionsJson = System.Text.Json.JsonSerializer.Serialize(decisions);
            meeting.UpdateLastModified();

            // 同步更新所有引用这条决策的任务的 SourceDecisionContent 快照
            var updatedContent = content;
            var relatedItems = await _context.MeetingActionItems
                .Where(x => x.MeetingMinutesId == meetingId && x.SourceDecisionIndex == decisionIndex + 1)
                .ToListAsync();
            foreach (var item in relatedItems)
            {
                item.SourceDecisionContent = updatedContent;
            }

            await _context.SaveChangesAsync();

            return new JsonResult(new { success = true, msg = "保存成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update decision {Idx} in meeting {MeetingId}", decisionIndex, meetingId);
            return new JsonResult(new { success = false, msg = "保存失败，请稍后重试" });
        }
    }

    // ====================== AI 重新匹配单条决策的原话 ======================
    public async Task<JsonResult> OnPostAIMatchDecisionQuotesAsync(int meetingId, int decisionIndex)
    {
        try
        {
            var meeting = await GetMeetingForActionItemWriteAsync(meetingId);
            if (meeting == null)
                return new JsonResult(new { success = false, msg = "无权限修改该会议" }) { StatusCode = StatusCodes.Status403Forbidden };

            if (meeting.ConfirmedAt.HasValue)
                return new JsonResult(new { success = false, msg = "会议已确认，不可再修改" });

            var decisions = DeserializeDecisions(meeting.AiDecisionsJson);
            if (decisionIndex < 0 || decisionIndex >= decisions.Count)
                return new JsonResult(new { success = false, msg = "决策序号无效" });

            var sourceText = string.IsNullOrWhiteSpace(meeting.TranscriptText) ? meeting.MeetingContent : meeting.TranscriptText;
            if (string.IsNullOrWhiteSpace(sourceText))
                return new JsonResult(new { success = false, msg = "会议没有转写/内容，无法匹配原话" });

            var decision = decisions[decisionIndex];

            // 策略：不截断整条转写。先定位讨论窗口中心，取 ±2000 字传给 AI。
            // 只用决策内容的关键词定位（决策人名字全篇都有，无定位价值）。
            // 选"最长且最稀有"的关键词——出现次数越少、越长越精准。
            string contextWindow;
            var keywords = MeetingQuoteEvidence.ExtractKeywordsForSearch(decision.Content);

            int bestCenter = -1;
            if (keywords.Count > 0)
            {
                // 先按长度从长到短、再按出现次数从少到多排序
                var ranked = keywords
                    .Select(k => new { kw = k, len = k.Length, count = CountOccurrences(sourceText, k) })
                    .Where(x => x.count > 0)
                    .OrderByDescending(x => x.len)
                    .ThenBy(x => x.count)
                    .ToList();

                if (ranked.Count > 0)
                {
                    // 用排第一的（最长最稀有）关键词的最后出现位置作为中心
                    bestCenter = sourceText.LastIndexOf(ranked[0].kw, StringComparison.Ordinal);
                }
            }

            if (bestCenter < 0) bestCenter = sourceText.Length / 2; // 退而取中

            const int halfWin = 2000;
            int winStart = Math.Max(0, bestCenter - halfWin);
            int winEnd = Math.Min(sourceText.Length, bestCenter + halfWin);
            contextWindow = sourceText[winStart..winEnd];

            var prompt = new System.Text.StringBuilder();
            prompt.AppendLine("你是会议原话证据标注专家。以下是会议转写中的一段讨论上下文，请从中找出支撑这条决策的完整讨论过程。");
            prompt.AppendLine();
            prompt.AppendLine("【讨论上下文】");
            prompt.AppendLine(contextWindow);
            prompt.AppendLine();
            prompt.AppendLine("【需要标注原话的决策】");
            prompt.AppendLine("决策内容：" + decision.Content);
            prompt.AppendLine("决策人：" + decision.DecisionMaker);
            prompt.AppendLine();
            prompt.AppendLine("请从转写中找出：");
            prompt.AppendLine("1. 决策人（decisionMaker）：在这条讨论中最终拍板/说结论的人。多人共同决策用顿号分隔（如\"乔宽、赵冬\"）。如果确实无法判断就填\"未明确\"");
            prompt.AppendLine("2. 最关键的一句原话（keyQuote）：直接促成决策定论的那句话（通常是决策人说的结论句）");
            prompt.AppendLine("3. 完整讨论链（originalQuotes）：围绕这条决策的讨论过程，包含：谁先提出/引入话题 -> 谁澄清/问关键问题 -> 谁发表意见/分歧 -> 谁达成一致/最终定论。每条原样保留发言人前缀（如\"乔宽:\"\"赵冬:\"），按时间顺序");
            prompt.AppendLine();
            prompt.AppendLine("严格返回纯 JSON，不要任何解释。原文中的引号请用单引号'代替，避免破坏 JSON 格式。JSON 示例：");
            prompt.AppendLine("{\"decisionMaker\": \"乔宽\", \"keyQuote\": \"乔宽：我觉得这个方案可以\", \"originalQuotes\": [\"乔宽：...\", \"赵冬：...\", \"乔宽：就这么定了\"]}");

            var rawAi = await _aiService.GetChatCompletionAsync(prompt.ToString());
            if (string.IsNullOrWhiteSpace(rawAi))
                return new JsonResult(new { success = false, msg = "AI 解析失败，空响应" });

            // 从返回文本里抽 JSON（可能被 markdown 包裹）
            var jsonStart = rawAi.IndexOf('{');
            var jsonEnd = rawAi.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < jsonStart)
                return new JsonResult(new { success = false, msg = "AI 返回格式异常：" + rawAi.Substring(0, Math.Min(80, rawAi.Length)) });
            var json = rawAi[jsonStart..(jsonEnd + 1)];

            string newKeyQuote = string.Empty;
            List<string> newQuotes = new();
            string? newDecisionMaker = null; // null 表示 AI 没返回，沿用原值
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("decisionMaker", out var dmEl) && dmEl.ValueKind == JsonValueKind.String)
                    newDecisionMaker = dmEl.GetString() ?? string.Empty;
                if (doc.RootElement.TryGetProperty("keyQuote", out var kqEl) && kqEl.ValueKind == JsonValueKind.String)
                    newKeyQuote = kqEl.GetString() ?? string.Empty;
                if (doc.RootElement.TryGetProperty("originalQuotes", out var oqEl) && oqEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in oqEl.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.String)
                            newQuotes.Add(e.GetString() ?? string.Empty);
                }
            }
            catch
            {
                // AI 有时在字符串值里残留双引号导致严格 JSON 解析失败，
                // 用正则 fallback 尽量把字段捞出来。
                _logger.LogWarning("严格 JSON 解析失败，尝试正则 fallback 原始响应前 200 字：{Snippet}", json.Length > 200 ? json.Substring(0, 200) : json);

                // decisionMaker
                var dm = System.Text.RegularExpressions.Regex.Match(json,
                    "\"decisionMaker\"\\s*:\\s*\"([^\"]{1,100})\"",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (dm.Success) newDecisionMaker = dm.Groups[1].Value;

                // 1) keyQuote：{"keyQuote": "xxx"， 非贪婪匹配到后面的引号
                var m = System.Text.RegularExpressions.Regex.Match(json,
                    "\"keyQuote\"\\s*:\\s*\"(?<q>[^\"]{10,1500})\"",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) newKeyQuote = m.Groups["q"].Value;

                // 2) originalQuotes 数组里的字符串元素：逐个捞 "xxx"
                var quotesMatches = System.Text.RegularExpressions.Regex.Matches(json,
                    "\"originalQuotes\"\\s*:\\s*\\[(?<body>[\\s\\S]*?)\\]");
                if (quotesMatches.Count > 0)
                {
                    var body = quotesMatches[0].Groups["body"].Value;
                    var itemMatches = System.Text.RegularExpressions.Regex.Matches(body,
                        "\"([^\"]{10,1500})\"");
                    foreach (System.Text.RegularExpressions.Match item in itemMatches)
                        newQuotes.Add(item.Groups[1].Value);
                }

                if (string.IsNullOrWhiteSpace(newKeyQuote) && newQuotes.Count == 0)
                    return new JsonResult(new { success = false, msg = "AI 返回的 JSON 解析失败" });
            }

            // 验真：AI 返回的原话不能是幻觉。
            // 策略：原话里必须有至少 2 个关键词（人名/名词短语）能在 sourceText 里找到，
            // 这样既防 AI 编造不存在的人名/实体，又允许 AI 对口语做正常的书面改写（不是逐字匹配）。
            if (!string.IsNullOrWhiteSpace(newKeyQuote)
                && !MeetingQuoteEvidence.HasEnoughKeywordCoverage(sourceText, newKeyQuote))
                newKeyQuote = string.Empty;
            newQuotes = newQuotes
                .Where(q => !string.IsNullOrWhiteSpace(q) && MeetingQuoteEvidence.HasEnoughKeywordCoverage(sourceText, q))
                .ToList();

            // AI 结果可能比原值差（返回空、验真后全清），做兜底：
            // 如果 AI 返回的 keyQuote 为空，保留原值；
            // 如果 AI 返回的 quotes 为空但原值不为空，保留原值。
            var originalKeyQuote = decision.KeyQuote ?? string.Empty;
            var originalQuotes = decision.OriginalQuotes ?? new List<string>();
            var originalMaker = decision.DecisionMaker ?? string.Empty;

            if (string.IsNullOrWhiteSpace(newKeyQuote))
                newKeyQuote = originalKeyQuote;
            if (newQuotes.Count == 0 && originalQuotes.Count > 0)
                newQuotes = originalQuotes;

            decision.KeyQuote = newKeyQuote;
            decision.OriginalQuote = newKeyQuote;
            decision.OriginalQuotes = newQuotes;
            // 决策人：AI 返回了就用，没返回就保留原值
            if (newDecisionMaker != null)
                decision.DecisionMaker = newDecisionMaker.Trim();
            else
                decision.DecisionMaker = originalMaker;

            meeting.AiDecisionsJson = System.Text.Json.JsonSerializer.Serialize(decisions);
            meeting.UpdateLastModified();
            await _context.SaveChangesAsync();

            return new JsonResult(new
            {
                success = true,
                msg = "AI 重新匹配完成",
                keyQuote = decision.GetKeyQuote(),
                quotes = decision.OriginalQuotes,
                decisionMaker = decision.DecisionMaker
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI re-match decision quotes failed meeting={Id} idx={Idx}", meetingId, decisionIndex);
            return new JsonResult(new { success = false, msg = "AI 匹配失败：" + (ex.InnerException?.Message ?? ex.Message) });
        }
    }

    public async Task<JsonResult> OnPostDeleteDecisionAsync(int meetingId, int decisionIndex)
    {
        try
        {
            var meeting = await GetMeetingForActionItemWriteAsync(meetingId);
            if (meeting == null)
                return new JsonResult(new { success = false, msg = "无权限修改该会议" }) { StatusCode = StatusCodes.Status403Forbidden };

            if (meeting.ConfirmedAt.HasValue)
                return new JsonResult(new { success = false, msg = "会议已确认写入任务，不可再修改决策" });

            var decisions = DeserializeDecisions(meeting.AiDecisionsJson);
            if (decisionIndex < 0 || decisionIndex >= decisions.Count)
                return new JsonResult(new { success = false, msg = "决策序号无效" });

            if (decisions.Count <= 1)
                return new JsonResult(new { success = false, msg = "至少保留一条决策" });

            decisions.RemoveAt(decisionIndex);

            meeting.AiDecisionsJson = System.Text.Json.JsonSerializer.Serialize(decisions);
            meeting.UpdateLastModified();

            // 同步修正关联任务的 SourceDecisionIndex：
            // - 原来指向被删决策的（值 == decisionIndex + 1）置为 null
            // - 原来序号更大的（值 > decisionIndex + 1）全部减 1
            int deleted1Based = decisionIndex + 1;
            var items = await _context.MeetingActionItems
                .Where(x => x.MeetingMinutesId == meetingId && x.SourceDecisionIndex.HasValue)
                .ToListAsync();
            foreach (var item in items)
            {
                if (item.SourceDecisionIndex == deleted1Based)
                {
                    item.SourceDecisionIndex = null;
                    item.SourceDecisionContent = null;
                }
                else if (item.SourceDecisionIndex > deleted1Based)
                {
                    item.SourceDecisionIndex = item.SourceDecisionIndex - 1;
                    // 同步刷新 SourceDecisionContent 快照指向的新决策内容
                    var newIdx = item.SourceDecisionIndex.Value; // 现在是 1 基
                    if (newIdx >= 1 && newIdx <= decisions.Count)
                    {
                        item.SourceDecisionContent = decisions[newIdx - 1].Content;
                    }
                }
            }

            await _context.SaveChangesAsync();

            return new JsonResult(new { success = true, msg = "删除成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete decision {Idx} in meeting {MeetingId}", decisionIndex, meetingId);
            return new JsonResult(new { success = false, msg = "删除失败，请稍后重试" });
        }
    }

    // ====================== 删除行动项（只返回JSON） ======================
    public async Task<IActionResult> OnPostDeleteActionItemAsync(int itemId, int meetingId)
    {
        try
        {
            if (itemId <= 0 || meetingId <= 0)
            {
                return new JsonResult(new { success = false, msg = "参数ID无效" });
            }

            if (await GetMeetingForActionItemWriteAsync(meetingId) == null)
            {
                return new JsonResult(new { success = false, msg = "你没有权限删除该会议的任务" })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
            }

            // 查找行动项
            var item = await _context.MeetingActionItems
                .FirstOrDefaultAsync(x => x.Id == itemId && x.MeetingMinutesId == meetingId);

            if (item == null)
            {
                return new JsonResult(new { success = false, msg = "未找到任务" });
            }

            if (item.IsConfirmed)
            {
                return new JsonResult(new
                {
                    success = false,
                    msg = "已确认的行动项属于审计记录，不能删除；如需调整请在正式任务中操作"
                });
            }

            // 待确认行动项只是会议建议，删除它不能隐式删除任何正式任务。
            _context.MeetingActionItems.Remove(item);
            await _context.SaveChangesAsync();

            return new JsonResult(new { success = true, msg = "删除成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete meeting action item {ItemId} in meeting {MeetingId}", itemId, meetingId);
            return new JsonResult(new { success = false, msg = "删除失败，请稍后重试" });
        }
    }

    // ====================== 弹窗编辑（跳转页面） ======================
    public async Task<IActionResult> OnPostUpdateActionItemAsync(
        int itemId,
        int meetingId,
        string title,
        string? description,
        string? assigneeId,
        string? deadline,
        string priority,
        string taskStatus,
        string? groupName)
    {
        try
        {
            var meeting = await GetMeetingForActionItemWriteAsync(meetingId);
            if (meeting == null) return Forbid();

            var item = await _context.MeetingActionItems.FindAsync(itemId);
            if (item == null || item.MeetingMinutesId != meetingId)
            {
                TempData["ErrorMessage"] = "修改失败，记录不存在或不属于当前会议";
                return RedirectToPage(new { id = meetingId });
            }

            // 保存原有的匹配任务ID
            var matchedTaskId = item.MatchedTaskId;

            title = title?.Trim() ?? string.Empty;
            if (title.Length == 0 || title.Length > 255)
            {
                TempData["ErrorMessage"] = "任务标题不能为空且不能超过255个字符";
                return RedirectToPage(new { id = meetingId });
            }
            if (!ValidPriorities.Contains(priority) || !ValidTaskStatuses.Contains(taskStatus))
            {
                TempData["ErrorMessage"] = "优先级或任务状态无效";
                return RedirectToPage(new { id = meetingId });
            }
            if (!TryParseDeadline(deadline, out var parsedDeadline)
                || !await TryResolveAssigneeAsync(meeting.ProjectId, assigneeId, meeting.Project?.LeaderUserId, item)
                || !await IsValidGroupAsync(meeting.ProjectId, groupName))
            {
                TempData["ErrorMessage"] = "责任人、截止日期或任务分组不属于当前项目";
                return RedirectToPage(new { id = meetingId });
            }

            // 更新行动项
            item.Title = title;
            item.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            item.Deadline = parsedDeadline;
            item.Priority = priority;
            item.TaskStatus = taskStatus;
            item.GroupName = string.IsNullOrEmpty(groupName) ? null : groupName;

            if (item.ActionType == "TaskChange")
            {
                item.AfterAssigneeId = item.AssigneeId;
                item.AfterDeadline = item.Deadline;
                item.AfterPriority = item.Priority;
                item.AfterStatus = item.TaskStatus;
            }

            // 如果已同步，同步更新对应的正式任务
            if (item.IsConfirmed && matchedTaskId.HasValue)
            {
                var updated = await UpdateMatchedTaskFullAsync(
                    matchedTaskId.Value,
                    meeting.ProjectId,
                    item,
                    assigneeId);
                if (!updated)
                {
                    TempData["ErrorMessage"] = "关联的正式任务不存在、已删除或不属于当前项目";
                    return RedirectToPage(new { id = meetingId });
                }
            }

            // 行动项与正式任务在同一次 SaveChanges 中原子提交。
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "任务项修改成功";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit meeting action item {ItemId} in meeting {MeetingId}", itemId, meetingId);
            TempData["ErrorMessage"] = "修改失败，请稍后重试";
        }

        return RedirectToPage(new { id = meetingId });
    }

    // ====================== 辅助方法：根据分组名称获取GroupId ======================
    private async Task<int?> GetGroupIdByNameAsync(int projectId, string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            return null;

        var group = await _context.TaskGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.ProjectId == projectId && !g.IsDeleted && g.Name == groupName);

        return group?.Id;
    }

    // ====================== 辅助方法：更新已同步的任务（行内编辑） ======================
    private async Task<bool> UpdateMatchedTaskAsync(
        int taskId,
        int projectId,
        string? assigneeId,
        string? deadline,
        string? priority,
        string? taskStatus,
        string? groupName)
    {
        var task = await _context.ToDoTasks.FirstOrDefaultAsync(candidate =>
            candidate.Id == taskId
            && candidate.ProjectId == projectId
            && !candidate.IsDeleted);
        if (task == null) return false;

        // 更新责任人
        if (assigneeId != null)
        {
            task.AssigneeId = int.TryParse(assigneeId, out var assigneeUserId)
                ? assigneeUserId
                : null;
        }

        // 更新截止日期
        if (deadline != null && TryParseDeadline(deadline, out var taskDeadline))
        {
            task.EndTime = taskDeadline;
        }

        // 更新优先级
        if (priority != null)
        {
            task.Priority = priority switch
            {
                "Low" => TaskPriority.Low,
                "Medium" => TaskPriority.Medium,
                "High" => TaskPriority.High,
                _ => task.Priority
            };
        }

        // 更新任务状态
        if (taskStatus != null)
        {
            var nextStatus = taskStatus switch
            {
                "NotStarted" => Entities.TaskStatus.NotStarted,
                "InProgress" => Entities.TaskStatus.InProgress,
                "Completed" => Entities.TaskStatus.Completed,
                "Cancelled" => Entities.TaskStatus.Cancelled,
                "PendingConfirmation" => Entities.TaskStatus.PendingConfirmation,
                _ => task.Status
            };
            task.SetStatus(nextStatus);
        }

        // 更新分组（通过GroupId）
        if (groupName != null)
        {
            var groupId = await GetGroupIdByNameAsync(projectId, groupName);
            task.GroupId = groupId;
        }

        task.UpdatedAt = AppTime.Now;
        return true;
    }

    // ====================== 辅助方法：更新已同步的任务（弹窗编辑） ======================
    private async Task<bool> UpdateMatchedTaskFullAsync(
        int taskId,
        int projectId,
        MeetingActionItem item,
        string? assigneeId)
    {
        var task = await _context.ToDoTasks.FirstOrDefaultAsync(candidate =>
            candidate.Id == taskId
            && candidate.ProjectId == projectId
            && !candidate.IsDeleted);
        if (task == null) return false;

        task.Title = item.Title;
        task.Description = item.Description;
        task.EndTime = item.Deadline;
        task.Priority = item.Priority switch
        {
            "Low" => TaskPriority.Low,
            "Medium" => TaskPriority.Medium,
            "High" => TaskPriority.High,
            _ => task.Priority
        };
        var nextStatus = item.TaskStatus switch
        {
            "NotStarted" => Entities.TaskStatus.NotStarted,
            "InProgress" => Entities.TaskStatus.InProgress,
            "Completed" => Entities.TaskStatus.Completed,
            "Cancelled" => Entities.TaskStatus.Cancelled,
            "PendingConfirmation" => Entities.TaskStatus.PendingConfirmation,
            _ => task.Status
        };
        task.SetStatus(nextStatus);

        // 更新分组（通过GroupId）
        if (!string.IsNullOrEmpty(item.GroupName))
        {
            var groupId = await GetGroupIdByNameAsync(projectId, item.GroupName);
            task.GroupId = groupId;
        }
        else
        {
            task.GroupId = null;
        }

        if (int.TryParse(assigneeId, out var assigneeUserId))
        {
            task.AssigneeId = assigneeUserId;
        }
        else
        {
            task.AssigneeId = null;
        }

        task.UpdatedAt = AppTime.Now;
        return true;
    }

    // ====================== AI解析 ======================
    public async Task<IActionResult> OnPostPrepareAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        var meeting = await _service.GetMeetingMinutesWithDetails(id);
        if (meeting == null || !await _service.CanAccessMeetingAsync(meeting, user))
            return Forbid();
        if (!await _service.CheckEditPermission(meeting, user, out _)
            && !await _syncService.CanApproveWritesAsync(meeting, user))
            return Forbid();

        var result = await _syncService.PrepareAsync(id, user.Id);
        TempData[result.Success ? "SuccessMessage" : "ErrorMessage"] = result.Success
            ? result.Items.Count > 0
                ? $"已生成 {result.Items.Count} 项待确认行动项，确认前不会修改项目任务。"
                : "未识别到明确的行动项。"
            : result.ErrorMessage;

        return RedirectToPage(new { id });
    }

    // ====================== 确认写入 ======================
    public async Task<IActionResult> OnPostConfirmAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        var result = await _syncService.ConfirmAsync(id, user.Id);

        // 开完会后，锁定关联的会前准备草稿（置为 Finalized，不可再编辑）
        if (result.Success)
        {
            var meeting = await _context.MeetingMinutes.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == id && m.PrepDraftId.HasValue);
            if (meeting?.PrepDraftId.HasValue == true)
            {
                var draft = await _context.MeetingPrepDrafts
                    .FirstOrDefaultAsync(d => d.Id == meeting.PrepDraftId.Value && !d.IsDeleted);
                if (draft != null && draft.Status != MeetingPrepDraftStatus.Finalized)
                {
                    draft.Status = MeetingPrepDraftStatus.Finalized;
                    draft.LastModifiedAt = AppTime.Now;
                    await _context.SaveChangesAsync();
                }
            }
        }

        TempData[result.Success ? "SuccessMessage" : "ErrorMessage"] = result.Success
            ? result.Items.Count > 0
                ? $"已确认并写入 {result.Items.Count} 项任务变更，会前准备草稿已锁定。"
                : "会议已确认提交。会前准备草稿已锁定。"
            : result.ErrorMessage;

        return RedirectToPage(new { id });
    }

    // ====================== 页面加载 ======================
    public ToDo.Entities.MeetingMinutes MeetingMinutes { get; set; } = new();
    public Project Project { get; set; } = new();
    public string CreatorName { get; set; } = string.Empty;
    public List<AttachmentViewModel> Attachments { get; set; } = new();
    public List<MeetingVersion> Versions { get; set; } = new();
    public List<MeetingActionItem> ActionItems { get; set; } = new();
    public bool CanEdit { get; set; }
    public bool CanApproveWrites { get; set; }
    public string RenderedContent { get; set; } = string.Empty;
    public List<ApplicationUser> ProjectMembers { get; set; } = new();
    // ===== 多项目改造：按项目分组的数据 =====
    public Dictionary<int, List<MeetingActionItem>> ActionItemsByProject { get; set; } = new();
    public Dictionary<int, Project> ProjectsById { get; set; } = new();
    public Dictionary<int, List<ApplicationUser>> ProjectMembersByProject { get; set; } = new();
    public Dictionary<int, List<DomainSelectListItem>> ProjectTaskGroupsByProject { get; set; } = new();
    /// <summary>从哪个项目列表进入详情（URL 参数）；不存在时回退到默认项目</summary>
    public int? FromProjectId { get; set; }
    public List<DomainSelectListItem> ProjectTaskGroups { get; set; } = new();
    public List<MeetingPrepDraftTaskItem> PrepDraftAllTasks { get; set; } = new();
    public List<MeetingPrepDraftTaskItem> UndiscussedDraftTasks { get; set; } = new();
    public MeetingPrepDraft? LinkedPrepDraft { get; set; }
    public List<MeetingDecisionDisplayItem> MeetingDecisions { get; set; } = new();
    public string DebugInfo { get; set; } = string.Empty;
    public string AIErrorInfo { get; set; } = string.Empty;


    public class MeetingPrepDraftTaskItem
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Status { get; set; }
        public string? ProjectName { get; set; }
        public string? AssigneeName { get; set; }
        public string? Deadline { get; set; }
        public bool IsOverdue { get; set; }
    }

    public class MeetingDecisionDisplayItem
    {
        public string Content { get; set; } = string.Empty;
        public string DecisionMaker { get; set; } = string.Empty;
        public string OriginalQuote { get; set; } = string.Empty;
        public List<string> OriginalQuotes { get; set; } = new();
        public string KeyQuote { get; set; } = string.Empty;

        public void ResolveEvidence(string sourceText)
        {
            var evidence = MeetingQuoteEvidence.Resolve(sourceText, OriginalQuotes, KeyQuote, OriginalQuote);
            OriginalQuotes = evidence.Quotes.ToList();
            KeyQuote = evidence.KeyQuote;
            OriginalQuote = evidence.KeyQuote;
        }

        /// <summary>用于前端渲染的引用列表：优先多段引用，兼容旧单条引用</summary>
        public List<string> GetQuotes()
        {
            var quotes = OriginalQuotes.Where(q => !string.IsNullOrWhiteSpace(q)).ToList();
            if (quotes.Count == 0 && !string.IsNullOrWhiteSpace(OriginalQuote))
            {
                quotes.Add(OriginalQuote);
            }
            return quotes;
        }

        /// <summary>列表展示用的最关键一句原话：优先 KeyQuote，兼容旧数据取结论句；超长截断到第一句/80字</summary>
        public string GetKeyQuote()
        {
            string raw;
            if (!string.IsNullOrWhiteSpace(KeyQuote)) raw = KeyQuote.Trim();
            else
            {
                var quotes = GetQuotes();
                raw = quotes.LastOrDefault()?.Trim() ?? string.Empty;
            }
            // 超长截断：优先取第一句（到句号/问号/感叹号），否则 80 字加省略号
            if (raw.Length > 80)
            {
                var firstSentenceEnd = raw.IndexOfAny(new[] { '。', '？', '！', '.', '?', '!' });
                if (firstSentenceEnd > 0 && firstSentenceEnd <= 80)
                    raw = raw.Substring(0, firstSentenceEnd + 1);
                else
                    raw = raw.Substring(0, 80) + "…";
            }
            return raw;
        }
    }

    /// <summary>反序列化会议决策 JSON，失败或无数据返回 null</summary>
    private static List<MeetingDecisionDisplayItem>? DeserializeDecisions(string? decisionsJson)
    {
        if (string.IsNullOrWhiteSpace(decisionsJson)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<MeetingDecisionDisplayItem>>(
                decisionsJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 任务/标题归一化匹配：忽略标点、空格、大小写，用于语义匹配。
    /// </summary>
    private static string NormalizeForMatch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var chars = text.Where(c => !char.IsPunctuation(c) && !char.IsWhiteSpace(c));
        return new string(chars.ToArray()).ToLowerInvariant();
    }

    /// <summary>
    /// 判断任务快照的来源决策内容与当前决策列表中的某条是否为同一条：
    /// 忽略标点空格大小写后完全一致，或一方包含另一方（快照可能被截断）。
    /// </summary>
    public static bool DecisionContentMatches(string? currentContent, string? snapshotContent)
    {
        var current = NormalizeForMatch(currentContent);
        var snapshot = NormalizeForMatch(snapshotContent);
        if (current.Length == 0 || snapshot.Length == 0) return false;
        return current == snapshot || current.Contains(snapshot) || snapshot.Contains(current);
    }

    public async Task<IActionResult> OnPostSuperviseAsync(int id)
    {
        var meeting = await GetMeetingForActionItemWriteAsync(id);
        if (meeting == null) return Forbid();

        var result = await _supervisionService.RunAsync(AppTime.Now, id);
        TempData["SuccessMessage"] = result.Evaluated == 0
            ? "当前没有已确认且可督办的会议任务"
            : $"督办检查完成：检查 {result.Evaluated} 项，状态变化 {result.StateChanges} 项，新增提醒 {result.NotificationsCreated} 条";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnGetAsync(int? id, int? fromProjectId = null)
    {
        if (!id.HasValue) return NotFound("未找到会议纪要");

        var meeting = await _service.GetMeetingMinutesWithDetails(id.Value);
        if (meeting == null) return NotFound("会议纪要不存在或已被删除");
        MeetingMinutes = meeting;

        var user = await _userManager.GetUserAsync(User);
        if (user == null || !await _service.CanAccessMeetingAsync(MeetingMinutes, user))
            return Forbid();
        // ===== 多项目改造：校验 fromProjectId 必须在关联项目里，否则回退 =====
        var meetingProjectIdsFromRelation = meeting.MeetingProjects.Select(mp => mp.ProjectId).ToList();
        if (meetingProjectIdsFromRelation.Count == 0)
            meetingProjectIdsFromRelation.Add(meeting.ProjectId);

        if (fromProjectId.HasValue && meetingProjectIdsFromRelation.Contains(fromProjectId.Value))
            FromProjectId = fromProjectId.Value;
        else
            FromProjectId = null;

        CanEdit = await _service.CheckEditPermission(MeetingMinutes, user, out _);
        CanApproveWrites = await _syncService.CanApproveWritesAsync(MeetingMinutes, user);

        Project = MeetingMinutes.Project!;
        CreatorName = await _service.GetCreatorName(MeetingMinutes.CreatorId);
        Attachments = await _service.GetAttachmentsByMeetingId(id.Value);
        Versions = MeetingMinutes.Versions.OrderByDescending(item => item.VersionNumber).ToList();
        // 直接查询获取最新的 ActionItems（包含 IsConfirmed 字段）
        ActionItems = await _context.MeetingActionItems
            .Where(a => a.MeetingMinutesId == id.Value)
            .Include(a => a.Assignee)
            .Include(a => a.MatchedTask).ThenInclude(task => task!.Assignee)
            .Include(a => a.MatchedTask).ThenInclude(task => task!.Group)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();
        // ===== 多项目改造：按项目分组 =====
        var meetingProjectIds = await _context.MeetingMinutesProjects
            .Where(mp => mp.MeetingMinutesId == id.Value)
            .Select(mp => mp.ProjectId)
            .ToListAsync();

        if (meetingProjectIds.Count == 0)
            meetingProjectIds.Add(MeetingMinutes.ProjectId);

        ProjectsById = await _context.Project
            .Where(p => meetingProjectIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        ActionItemsByProject = ActionItems
            .GroupBy(a => a.ProjectId ?? MeetingMinutes.ProjectId)
            .ToDictionary(g => g.Key, g => g.ToList());

        ProjectMembersByProject = new();
        ProjectTaskGroupsByProject = new();
        foreach (var pid in meetingProjectIds)
        {
            ProjectMembersByProject[pid] = await _context.ProjectUsers
                .Where(pu => pu.ProjectId == pid)
                .Include(pu => pu.User)
                .Select(pu => pu.User!)
                .ToListAsync();

            ProjectTaskGroupsByProject[pid] = await _taskDomainService
                .GetProjectTaskGroupSelectListAsync(pid);
        }
        // Markdown渲染
        var markdownPipeline = new MarkdownPipelineBuilder()
            .DisableHtml()
            .UseAdvancedExtensions()
            .Build();
        RenderedContent = Markdown.ToHtml(MeetingMinutes.MeetingContent ?? string.Empty, markdownPipeline);

        // 解析会议决策JSON
        if (!string.IsNullOrWhiteSpace(MeetingMinutes.AiDecisionsJson))
        {
            try
            {
                MeetingDecisions = System.Text.Json.JsonSerializer.Deserialize<List<MeetingDecisionDisplayItem>>(
                    MeetingMinutes.AiDecisionsJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<MeetingDecisionDisplayItem>();

                // 历史 JSON 也必须在展示前重新验真；无法定位回会议源文本的内容不再标记为“原话”。
                var decisionSource = string.IsNullOrWhiteSpace(MeetingMinutes.TranscriptText)
                    ? MeetingMinutes.MeetingContent
                    : MeetingMinutes.TranscriptText;
                foreach (var decision in MeetingDecisions)
                    decision.ResolveEvidence(decisionSource ?? string.Empty);
            }
            catch
            {
                MeetingDecisions = new List<MeetingDecisionDisplayItem>();
            }
        }

        ProjectMembers = await _context.ProjectUsers
            .Where(pu => pu.ProjectId == MeetingMinutes.ProjectId)
            .Include(pu => pu.User)
            .Select(pu => pu.User!)
            .ToListAsync();

        ProjectTaskGroups = await _taskDomainService.GetProjectTaskGroupSelectListAsync(MeetingMinutes.ProjectId);

        // 关联的会前准备草稿 → 计算"未讨论到的任务"
        if (MeetingMinutes.PrepDraftId.HasValue)
        {
            LinkedPrepDraft = await _context.MeetingPrepDrafts
                .FirstOrDefaultAsync(d => d.Id == MeetingMinutes.PrepDraftId.Value && !d.IsDeleted);

            if (LinkedPrepDraft != null && !string.IsNullOrWhiteSpace(LinkedPrepDraft.TaskSnapshotJson))
            {
                PrepDraftAllTasks = JsonSerializer.Deserialize<List<MeetingPrepDraftTaskItem>>(
                    LinkedPrepDraft.TaskSnapshotJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

                // 第一步：先用关键词匹配把"明显对应的"行动项自动关联到草稿任务
                // （新创建的会议还没人手动关联，自动兜底让用户不用手动一条条选）
                var alreadyAutoLinked = new HashSet<int>(); // 草稿任务ID，防止一个草稿任务被多条行动项抢
                var alreadyAutoLinkedActionItem = new HashSet<int>(); // 行动项ID，一个行动项只能关联一个草稿任务

                if (!MeetingMinutes.ConfirmedAt.HasValue)
                {
                    foreach (var draftTask in PrepDraftAllTasks.Where(t => t.Id > 0))
                    {
                        if (alreadyAutoLinked.Contains(draftTask.Id)) continue;

                        // 找 PrepDraftTaskId 已经显式设了的——跳过，尊重用户手动关联
                        if (ActionItems.Any(a => a.PrepDraftTaskId == draftTask.Id))
                        {
                            alreadyAutoLinked.Add(draftTask.Id);
                            continue;
                        }

                        var draftKeywords = MeetingQuoteEvidence.ExtractKeywordsForSearch(draftTask.Title);
                        if (draftKeywords.Count == 0) continue;

                        // 找一条还没被自动关联的、关键词重叠 ≥2 的行动项
                        foreach (var ai in ActionItems.Where(a => !a.PrepDraftTaskId.HasValue && !alreadyAutoLinkedActionItem.Contains(a.Id)))
                        {
                            var actKeywords = MeetingQuoteEvidence.ExtractKeywordsForSearch(
                                (ai.Title ?? string.Empty) + " " + (ai.Description ?? string.Empty));
                            int overlap = draftKeywords.Intersect(actKeywords, StringComparer.Ordinal).Count();
                            if (overlap >= 2)
                            {
                                ai.PrepDraftTaskId = draftTask.Id;
                                alreadyAutoLinked.Add(draftTask.Id);
                                alreadyAutoLinkedActionItem.Add(ai.Id);
                                break;
                            }
                        }
                    }

                    if (alreadyAutoLinked.Count > 0)
                        await _context.SaveChangesAsync();
                }

                // 第二步：未讨论到的 = 草稿里的任务，没有任何行动项通过 PrepDraftTaskId 关联它
                var linkedDraftIds = ActionItems
                    .Where(i => i.PrepDraftTaskId.HasValue)
                    .Select(i => i.PrepDraftTaskId!.Value)
                    .ToHashSet();

                UndiscussedDraftTasks = PrepDraftAllTasks
                    .Where(t => t.Id > 0 && !linkedDraftIds.Contains(t.Id))
                    .ToList();
            }
        }

        return Page();
    }

    private static readonly HashSet<string> ValidPriorities =
        new(StringComparer.Ordinal) { "Low", "Medium", "High" };

    private static readonly HashSet<string> ValidTaskStatuses =
        new(StringComparer.Ordinal) { "NotStarted", "InProgress", "Completed", "Cancelled", "PendingConfirmation" };

    private async Task<ToDo.Entities.MeetingMinutes?> GetMeetingForActionItemWriteAsync(int meetingId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return null;

        var meeting = await _context.MeetingMinutes
            .Include(item => item.Project)
            .FirstOrDefaultAsync(item => item.Id == meetingId && !item.IsDeleted);
        if (meeting == null || !await _service.CanAccessMeetingAsync(meeting, user)) return null;
        return await _syncService.CanApproveWritesAsync(meeting, user) ? meeting : null;
    }

    private async Task<bool> TryResolveAssigneeAsync(
        int projectId,
        string? assigneeId,
        int? projectLeaderId,
        MeetingActionItem item)
    {
        if (string.IsNullOrWhiteSpace(assigneeId))
        {
            item.AssigneeId = null;
            return true;
        }

        if (!int.TryParse(assigneeId, out var parsedId) || parsedId <= 0) return false;
        var belongsToProject = projectLeaderId == parsedId || await _context.ProjectUsers
            .AsNoTracking()
            .AnyAsync(member => member.ProjectId == projectId && member.UserId == parsedId);
        if (!belongsToProject) return false;

        item.AssigneeId = parsedId;
        return true;
    }

    private async Task<bool> IsValidGroupAsync(int projectId, string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName)) return true;
        return await _context.TaskGroups.AsNoTracking().AnyAsync(group =>
            group.ProjectId == projectId && !group.IsDeleted && group.Name == groupName);
    }

    private static bool TryParseDeadline(string? value, out DateTime? deadline)
    {
        deadline = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)) return false;
        deadline = parsed;
        return true;
    }

    private static int CountOccurrences(string source, string substring)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(substring)) return 0;
        int count = 0, idx = 0;
        while ((idx = source.IndexOf(substring, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += substring.Length;
        }
        return count;
    }
}

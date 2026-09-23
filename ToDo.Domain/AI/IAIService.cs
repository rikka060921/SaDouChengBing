using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ToDo.Domain.AI
{
    // 任务拆分结果DTO
    public class AITaskSplitResult
    {
        public List<string> SubTasks { get; set; } = new();
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    // 会议纪要处理结果DTO
    public class AIMeetingSummaryResult
    {
        public string KeyPoints { get; set; } = string.Empty;
        public List<AIMeetingTodo> TodoItems { get; set; } = new();
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public class AIMeetingTodo
    {
        public string Content { get; set; } = string.Empty;
        public string Assignee { get; set; } = string.Empty;
        public DateTime? Deadline { get; set; }
    }

    // 基础日报输入DTO
    public class AIDailyReportInput
    {
        public List<string> CompletedTasks { get; set; } = new();
        public List<string> UncompletedTasks { get; set; } = new();
        public List<string> Problems { get; set; } = new();
        public List<string> ActivityNotes { get; set; } = new();
        public DateTime ReportDate { get; set; }
    }

    // 日报输出结果DTO
    public class AIDailyReportResult
    {
        public string GeneratedReport { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public sealed class AIChatOptions
    {
        public string ModelName { get; set; } = string.Empty;
        public double Temperature { get; set; } = 0.3;
        public int MaxTokens { get; set; } = 6000;
        public int TimeoutSeconds { get; set; } = 90;
        public List<AIChatToolDefinition> Tools { get; set; } = new();
    }

    public sealed class AIChatToolDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public JsonElement Parameters { get; set; }
    }

    public sealed class AICompletionToolCall
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ArgumentsJson { get; set; } = "{}";
    }

    public sealed class AICompletionResult
    {
        public string Content { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public string FinishReason { get; set; } = string.Empty;
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public decimal? EstimatedCost { get; set; }
        public List<AICompletionToolCall> ToolCalls { get; set; } = new();
    }

    // 按项目汇总日报输入DTO
    public class DailyReportByProjectInput
    {
        public DateTime ReportDate { get; set; }
        public int ReporterId { get; set; }
        public List<ProjectDailyData> ProjectDailyDatas { get; set; } = new List<ProjectDailyData>();
    }

    /// <summary>
    /// 单个项目当日数据（合并后：保留第二段新增的 Description 字段）
    /// </summary>
    public class ProjectDailyData
    {
        public int ProjectId { get; set; }
        public string ProjectName { get; set; } = string.Empty;
        public string ProjectCreator { get; set; } = string.Empty;
        public string ProjectLeader { get; set; } = string.Empty;
        public string ProjectDescription { get; set; } = string.Empty;
        public DateTime ProjectCreateTime { get; set; }

        public List<TaskDailyInfo> NewTasks { get; set; } = new List<TaskDailyInfo>();
        public List<TaskDailyInfo> CompletedTasks { get; set; } = new List<TaskDailyInfo>();
        public List<TaskDailyInfo> UpdatedTasks { get; set; } = new List<TaskDailyInfo>();

        // 合并独有差异字段
        public string? Description { get; set; }

        public List<MeetingDailyInfo> NewMeetings { get; set; } = new List<MeetingDailyInfo>();
        public List<ReportDailyInfo> NewReports { get; set; } = new List<ReportDailyInfo>();
    }

    /// <summary>
    /// 任务当日信息
    /// </summary>
    public class TaskDailyInfo
    {
        public int TaskId { get; set; }
        public string TaskTitle { get; set; } = string.Empty;
        public string TaskGroupName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Priority { get; set; } = string.Empty;
        public string CreatorName { get; set; } = string.Empty;
        public string AssigneeName { get; set; } = string.Empty;
        public DateTime OperateTime { get; set; }
        public string TaskDescription { get; set; } = string.Empty;
    }

    /// <summary>
    /// 会议当日信息
    /// </summary>
    public class MeetingDailyInfo
    {
        public int MeetingId { get; set; }
        public string MeetingTitle { get; set; } = string.Empty;
        public string MeetingContent { get; set; } = string.Empty;
        public string CreatorName { get; set; } = string.Empty;
        public DateTime CreateTime { get; set; }
    }

    /// <summary>
    /// 项目日报当日信息
    /// </summary>
    public class ReportDailyInfo
    {
        public int ReportId { get; set; }
        public string ReportTitle { get; set; } = string.Empty;
        public string ReportContent { get; set; } = string.Empty;
        public string ReporterName { get; set; } = string.Empty;
        public DateTime CreateTime { get; set; }
    }

    /// <summary>
    /// 团队汇报输入DTO：按项目汇总各成员个人日报
    /// </summary>
    public class TeamReportInput
    {
        public DateTime ReportDate { get; set; }
        public int ProjectId { get; set; }
        public string ProjectName { get; set; } = string.Empty;
        public string ProjectLeader { get; set; } = string.Empty;
        /// <summary>各成员在该项目下的个人日报摘要集合</summary>
        public List<MemberSummaryInfo> Members { get; set; } = new();
        /// <summary>项目下的任务组快照（有任务组才传，无则空列表）</summary>
        public List<TaskGroupSnapshotInfo> TaskGroups { get; set; } = new();
    }

    /// <summary>
    /// 单个成员的个人日报摘要
    /// </summary>
    public class MemberSummaryInfo
    {
        public string UserName { get; set; } = string.Empty;
        /// <summary>该成员当日是否生成了个人日报（false 表示今日无日报，需在汇报中明确标注）</summary>
        public bool HasDailyReport { get; set; }
        /// <summary>该成员在本项目的分项总结</summary>
        public string ProjectSummary { get; set; } = string.Empty;
        /// <summary>该成员的完整个人日报内容</summary>
        public string TotalSummary { get; set; } = string.Empty;
    }

    /// <summary>
    /// 单个任务组快照（含描述和组内任务情况），用于团队汇报按任务组汇总
    /// </summary>
    public class TaskGroupSnapshotInfo
    {
        /// <summary>任务组名称</summary>
        public string GroupName { get; set; } = string.Empty;
        /// <summary>任务组描述（原定目标）</summary>
        public string GroupDescription { get; set; } = string.Empty;
        /// <summary>该组内任务总数</summary>
        public int TotalTaskCount { get; set; }
        /// <summary>已完成任务数</summary>
        public int CompletedTaskCount { get; set; }
        /// <summary>该组整体进度百分比（0-100）</summary>
        public int OverallProgressPercent { get; set; }
        /// <summary>该组下各任务的精简信息，格式：任务名｜状态｜进度%｜负责人</summary>
        public List<string> TaskSnapshots { get; set; } = new();
    }


    /// <summary>
    /// 单条解析子任务项
    /// </summary>
    public class TaskParseItem
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Deadline { get; set; } = string.Empty;
        public string Priority { get; set; } = "Medium";
        public string Status { get; set; } = "NotStarted";
        public string Group { get; set; } = string.Empty;
    }

    /// <summary>
    /// AI服务统一接口
    /// </summary>
    public interface IAIService
    {
        // 1. 任务自动拆分
        Task<AITaskSplitResult> SplitTaskAsync(string taskTitle, string taskDescription, string? expectedSubTaskCount = null);

        // 2. 【旧兼容】简易会议纪要处理（保留兜底）
        Task<AIMeetingSummaryResult> ProcessMeetingMinutesAsync(string meetingContent);

        Task<AIMeetingFullParseResult> ProcessMeetingMinutesFullStructAsync(
            string meetingContent,
            List<string>? memberNames = null,
            List<string>? projectNames = null);   // 新增

        // 3. 通用AI对话
        Task<string> GetChatCompletionAsync(string prompt);
        Task<string> GetChatCompletionAsync(string prompt, AIChatOptions options, CancellationToken cancellationToken = default);
        Task<AICompletionResult> GetChatCompletionWithUsageAsync(string prompt, AIChatOptions options, CancellationToken cancellationToken = default);

        // 4. 基础日报生成
        Task<AIDailyReportResult> GenerateDailyReportAsync(AIDailyReportInput input);

        // 5. 按项目分类日报
        Task<AIDailyReportResult> GenerateDailyReportByProjectAsync(DailyReportByProjectInput input);

        // 6. 智能文本解析结构化任务
        Task<AITaskParseResult> ParseTaskTextAsync(string text, string? expectedCount = null);

        // 7. 团队汇报：基于各成员个人日报按项目汇总
        Task<AIDailyReportResult> GenerateTeamReportAsync(TeamReportInput input);
        // 在 IAIService 接口中添加
        Task<AIMeetingMinutesResult> GenerateMeetingMinutesAsync(
            string transcript,
            string template,
            int projectId,
            CancellationToken cancellationToken = default);
    }
    // 会议纪要生成结果
    public class AIMeetingMinutesResult
    {
        public bool Success { get; set; }
        public string Content { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
    }

    /// <summary>会议纪要解析单条任务项，完全对齐文本任务解析结构</summary>
    public class MeetingTaskParseItem
    {
        /// <summary>任务简短标题</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>详细描述</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>截止日期 yyyy-MM-dd，无则null</summary>
        public string? Deadline { get; set; }

        /// <summary>优先级 High/Medium/Low</summary>
        public string Priority { get; set; } = "Medium";

        /// <summary>状态 NotStarted/InProgress/Completed</summary>
        public string Status { get; set; } = "NotStarted";

        /// <summary>任务分组</summary>
        public string? Group { get; set; }

        /// <summary>责任人姓名</summary>
        public string? AssigneeName { get; set; }

        /// <summary>
        /// 任务来源决策的序号（对应 Decisions 数组，从1开始）。
        /// 任务由某条会议决策的落地执行产生时填写；不来源于任何决策时为 null。
        /// </summary>
        public int? SourceDecisionIndex { get; set; }
        /// <summary>
        /// AI 判定的任务所属项目名（从会议关联的项目列表中选一个）。
        /// 单项目会议时可为 null。
        /// </summary>
        public string? ProjectName { get; set; }
    }

    /// <summary>会议完整结构化解析返回结果</summary>
    public class AIMeetingFullParseResult
    {
        public bool Success { get; set; }
        public string KeyPoints { get; set; } = string.Empty;
        public List<MeetingTaskParseItem> TaskItems { get; set; } = new();
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>开会原因总结</summary>
        public string MeetingPurpose { get; set; } = string.Empty;

        /// <summary>会议决策列表</summary>
        public List<MeetingDecisionItem> Decisions { get; set; } = new();
    }

    /// <summary>会议决策项</summary>
    public class MeetingDecisionItem
    {
        /// <summary>决策内容</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>决策人</summary>
        public string DecisionMaker { get; set; } = string.Empty;

        /// <summary>决策结论原话（旧字段，兼容历史数据）</summary>
        public string OriginalQuote { get; set; } = string.Empty;

        /// <summary>决策相关的原文引用片段列表（讨论过程关键发言 + 结论句）</summary>
        public List<string> OriginalQuotes { get; set; } = new();

        /// <summary>决策最关键的一句原话（直接促成决策定论的那句话），用于列表展示</summary>
        public string KeyQuote { get; set; } = string.Empty;
    }
}

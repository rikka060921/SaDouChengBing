using System;
using System.Collections.Generic;

namespace ToDo.Entities.DailySummary
{
    /// <summary>
    /// 每日汇总AI调用输入参数
    /// 用于封装当日项目、任务、会议、日报原始数据，构建AI提示词
    /// </summary>
    public class DailySummaryAiInput
    {
        /// <summary>
        /// 汇总日期
        /// </summary>
        public DateTime ReportDate { get; set; }

        /// <summary>
        /// 当日需汇总的项目数据集合
        /// </summary>
        public List<ProjectDailyBasicData> ProjectDailyDatas { get; set; } = new List<ProjectDailyBasicData>();
    }

    /// <summary>
    /// 项目每日基础数据（仅封装AI所需字段）
    /// </summary>
    public class ProjectDailyBasicData
    {
        /// <summary>
        /// 项目ID
        /// </summary>
        public int ProjectId { get; set; }

        /// <summary>
        /// 项目名称
        /// </summary>
        public string ProjectName { get; set; } = string.Empty;

        /// <summary>
        /// 项目创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 项目负责人姓名
        /// </summary>
        public string LeaderUserName { get; set; } = string.Empty;

        /// <summary>
        /// 项目描述
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 当日项目相关任务
        /// </summary>
        public List<TaskBasicInfo> DailyTasks { get; set; } = new List<TaskBasicInfo>();

        /// <summary>
        /// 当日项目会议纪要
        /// </summary>
        public List<MeetingBasicInfo> DailyMeetings { get; set; } = new List<MeetingBasicInfo>();

        /// <summary>
        /// 当日项目日报
        /// </summary>
        public List<ReportBasicInfo> DailyReports { get; set; } = new List<ReportBasicInfo>();
    }

    /// <summary>
    /// 任务基础信息（AI所需字段）
    /// </summary>
    public class TaskBasicInfo
    {
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? AssigneeName { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>
    /// 会议纪要基础信息（AI所需字段）
    /// </summary>
    public class MeetingBasicInfo
    {
        public string MeetingTitle { get; set; } = string.Empty;
        public string MeetingContent { get; set; } = string.Empty;
    }

    /// <summary>
    /// 日报基础信息（AI所需字段）
    /// </summary>
    public class ReportBasicInfo
    {
        public string ReportTitle { get; set; } = string.Empty;
        public string ReportContent { get; set; } = string.Empty;
    }
}
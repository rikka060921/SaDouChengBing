using ToDo.Entities;

namespace ToDo.Domain;

/// <summary>只解释已注册的能力与权限，不把宣传文案当成真实能力。</summary>
public static class AgentBusinessSummary
{
    public static IReadOnlyList<string> Capabilities(AgentDefinition agent)
        => AgentAdministrationService.ParseCapabilities(agent.CapabilitiesJson).Select(cap => cap switch
        {
            "task.read" or "project.analysis" => "进度与任务分析",
            "quality.review" or "delivery.acceptance" => "交付检查",
            "daily-report" or "report.create" => "日报 / 周报整理",
            "meeting-summary" => "会议内容整理",
            "document.read" => "项目资料整理",
            "web.search" => "公开资料调研",
            "task.create" => "拆分与创建任务",
            "task.update" => "调整任务",
            "project.update" => "更新项目信息",
            "project.document.write" => "编写项目资料",
            "task.comment" or "task.add_comment" => "任务反馈",
            "meeting.action.create" => "整理会议行动项",
            "meeting.supervision" => "任务跟进",
            _ => "其他已配置能力"
        }).Distinct().ToList();

    public static IReadOnlyList<string> Actions(AgentDefinition agent)
    {
        var catalog = new AgentToolCatalog();
        return agent.ToolPermissions.Where(t => t.IsEnabled).Select(t =>
        {
            var tool = catalog.Get(t.ToolName);
            if (tool == null) return "未识别工具（需维护人员检查）";
            var configured = t.RequiresApproval ? AgentToolReviewMode.HumanApproval : t.ReviewMode;
            var mode = configured < tool.MinimumReviewMode ? tool.MinimumReviewMode : configured;
            return tool.DisplayName + (mode == AgentToolReviewMode.HumanApproval ? " · 需人工审批"
                : mode == AgentToolReviewMode.AiReview ? " · 系统检查后执行" : " · 授权范围内执行");
        }).ToList();
    }
}

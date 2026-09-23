namespace ToDo.Domain;

/// <summary>可解释的用途映射，不调用模型自评、不使用负载或历史质量作为前置评分。</summary>
public static class AgentTaskIntentMatcher
{
    public sealed record Intent(string Label, string[] Capabilities, string? RequiredTool = null, bool RequiresDocuments = false);

    public static IReadOnlyList<Intent> Recognize(string text)
    {
        bool Has(params string[] terms) => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
        var intents = new List<Intent>();
        if (!Has("禁止联网", "不要联网", "不联网", "不得调用工具", "no web", "offline") && new[] { "联网", "网络搜索", "网上搜索", "搜索公开", "检索公开", "web search", "search the web" }
            .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)))
            intents.Add(new Intent("公开资料搜索", ["web.search"], "web.search"));
        if (Has("修改项目", "更新项目", "项目改名", "项目名称改", "项目名改"))
            intents.Add(new("修改项目信息", ["project.update"], "project.update"));
        if (Has("修改任务", "更新任务", "调整任务", "任务状态改", "更换负责人", "调整截止"))
            intents.Add(new("修改现有任务", ["task.update"], "task.update"));
        if (Has("拆分", "分解任务", "新建任务", "创建任务"))
            intents.Add(new("创建或拆分任务", ["task.create"], "task.create"));
        if (Has("文档", "资料", "概要设计", "详细设计", "需求说明", "需求分析")
            && Has("写", "生成", "保存", "编制", "更新", "修改"))
            intents.Add(new("编写项目资料", ["project.document.write"], "project.document.write"));
        if (Has("日报", "周报", "月报", "汇报", "报告"))
            intents.Add(new("整理工作报告", ["daily-report", "report.create"]));
        if (Has("创建行动项", "新建行动项"))
            intents.Add(new("创建会议行动项", ["meeting.action.create"], "meeting.action.create"));
        if (intents.Count > 0) return intents;
        if (Has("督办", "催办", "逾期", "阻塞"))
            return [new("任务督办", ["meeting.supervision"])];
        if (Has("验收", "交付检查"))
            return [new("交付检查", ["delivery.acceptance", "quality.review"])];
        if (Has("审查", "复核", "风险分析", "风险评估"))
            return [new("任务风险复核", ["task.comment", "task.add_comment", "quality.review"])];
        if (Has("会议", "纪要", "行动项", "议程"))
            return [new("会议内容整理", ["meeting-summary", "task-sync"])];
        if (Has("文档", "资料", "材料", "需求分析", "概要设计"))
            return [new("读取和整理项目资料", ["document.read"], RequiresDocuments: true)];
        if (Has("评论", "反馈"))
            return [new("任务反馈", ["task.comment", "task.add_comment"])];
        if (Has("任务", "进度", "计划"))
            return [new("任务与进度分析", ["task.read", "project.analysis"])];
        return [];
    }
}

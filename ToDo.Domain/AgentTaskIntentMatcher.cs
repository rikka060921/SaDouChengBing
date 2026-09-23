using System.Text.RegularExpressions;
using ToDo.Entities;

namespace ToDo.Domain;

/// <summary>用途与副作用分离。这里只解释用户需求，不授予项目权限或绕过工具审批。</summary>
public static class AgentTaskIntentMatcher
{
    public sealed record Intent(string Label, string[] Capabilities, string? RequiredTool = null, bool RequiresDocuments = false);
    public sealed record Decision(IReadOnlyList<Intent> Intents, IReadOnlySet<string> Tools, bool ConfirmPlan)
    {
        public bool Writes => Tools.Any(t => t != "web.search");
        public bool RequiresPlan => ConfirmPlan || Writes;
    }

    // 自动派单提示包含系统规则，不能把规则中的“写回”误当作用户授权。
    public static Decision ForTask(ToDoTask task, string? prompt = null, string? feedback = null)
    {
        var source = $"{task.Title}\n{task.Description}";
        if (!string.IsNullOrWhiteSpace(prompt)
            && !prompt.Contains("只有任务描述明确要求把结果写回系统时才允许调用工具", StringComparison.Ordinal))
            source += "\n" + prompt;
        var original = Analyze(source);
        if (string.IsNullOrWhiteSpace(feedback)) return original;
        var adjusted = Analyze(source + "\n" + feedback);
        // 调整意见可以缩小范围，不能替代任务描述授权新动作。
        return adjusted with { Tools = adjusted.Tools.Intersect(original.Tools).ToHashSet(StringComparer.OrdinalIgnoreCase) };
    }

    public static IReadOnlyList<Intent> Recognize(string text) => Analyze(text).Intents;

    public static Decision Analyze(string text)
    {
        text ??= string.Empty;
        bool Has(params string[] words) => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        bool Match(string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var intents = new List<Intent>();
        var confirm = Has("人工确认计划", "先确认计划", "确认计划后", "先给我确认", "先给我执行方案", "先不要执行", "approve plan first");
        var readOnly = Has("只读", "只读取", "不修改", "不要修改", "禁止修改", "不得修改", "不要写入", "不得写入", "禁止写入", "不写回", "不得调用工具",
            "只给建议", "只给出建议", "仅给建议", "仅提供建议", "只要建议", "只给草稿", "先给我看草稿", "先给我看",
            "read-only", "readonly", "do not write", "don't write", "must not write", "without writing");
        bool Denied(string action) => Match($@"(?:不要|不得|禁止|无需|不必|不能|暂不|不)(?:再|实际|直接)?{action}");
        var noSave = Denied("(?:保存|写入|写回)");
        var noCreate = Denied("(?:创建|新建|新增)");
        bool Save() => !noSave && Has("保存到", "保存至", "存入", "写入", "写回", "记录到系统", "同步到系统", "并保存");

        if (!Has("禁止联网", "不要联网", "不联网", "不得调用工具", "no web", "offline")
            && Has("联网", "网络搜索", "网上搜索", "搜索公开", "检索公开", "web search", "search the web"))
        {
            tools.Add("web.search");
            intents.Add(new("公开资料搜索", ["web.search"], "web.search"));
        }
        var documents = Has("文档", "资料", "概要设计", "详细设计", "需求说明", "需求分析");
        var report = Has("日报", "周报", "月报", "汇报", "报告");
        var taskIdeas = Has("拆分", "分解任务", "任务建议", "子任务");
        void AddWrite(string tool, string label, bool required = true)
        {
            tools.Add(tool);
            intents.Add(new(label, tool == "report.create" ? ["daily-report", "report.create"] : [tool], required ? tool : null));
        }
        if (!readOnly)
        {
            if (!Denied("(?:修改|更新|调整|更换|变更)") && Has("修改项目", "更新项目", "项目改名", "项目名称改", "项目名改")
                && !documents) AddWrite("project.update", "修改项目信息");
            if (!Denied("(?:修改|更新|调整|更换|变更)") && Has("修改任务", "更新任务", "调整任务", "任务状态改", "更换负责人", "调整截止"))
                AddWrite("task.update", "修改现有任务");
            if (!noCreate && Match(@"(?:创建|新建|新增)(?:这|那|上述|以下|的|\d|[一二三四五六七八九十]|个|条|项|子|\s){0,15}任务"))
                AddWrite("task.create", "创建任务记录");
            if (documents && !noSave && (Save() || Match(@"(?:修改|更新|覆盖)(?:项目资料|资料库)")))
                AddWrite("project.document.write", "保存或修改项目资料");
            if (report && !noCreate && (Save() || Has("创建日报", "创建周报", "创建月报", "创建报告")))
                AddWrite("report.create", "保存工作报告");
            if (!noCreate && Has("创建行动项", "新建行动项")) AddWrite("meeting.action.create", "创建会议行动项");
            if (!Denied("(?:添加|发布|新增)") && (Has("添加评论", "发布评论", "新增评论")
                || Has("评论") && Save() || Match(@"(?:write|post|add)\b[^\r\n.!?]{0,60}\bcomment")))
                AddWrite("task.add_comment", "发布任务评论");
        }

        // 写入意图已经表达对应用途；生成内容本身不要求拥有写工具。
        if (documents && !tools.Contains("project.document.write")
            && (!tools.Contains("web.search") || Has("项目资料", "项目文档", "需求分析", "概要设计", "详细设计")))
            intents.Add(new("读取资料与生成草稿", ["document.read", "project.document.write"], RequiresDocuments: true));
        if (taskIdeas && !tools.Contains("task.create") && !documents)
            intents.Add(new("任务拆分建议", ["task.read", "project.analysis", "task.create"]));
        if (report && !tools.Contains("report.create")) intents.Add(new("整理工作报告", ["daily-report", "report.create"]));
        if (intents.Count == 0)
        {
            if (Has("督办", "催办", "逾期", "阻塞")) intents.Add(new("任务督办", ["meeting.supervision"]));
            else if (Has("验收", "交付检查")) intents.Add(new("交付检查", ["delivery.acceptance", "quality.review"]));
            else if (Has("审查", "复核", "风险")) intents.Add(new("任务风险复核", ["project.analysis", "task.comment", "task.add_comment", "quality.review"]));
            else if (Has("会议", "纪要", "行动项", "议程")) intents.Add(new("会议内容整理", ["meeting-summary", "task-sync"]));
            else if (Has("评论", "反馈")) intents.Add(new("任务反馈", ["task.comment", "task.add_comment"]));
            else if (Has("任务", "进度", "计划", "项目", "执行方案")) intents.Add(new("任务与进度分析", ["task.read", "project.analysis"]));
        }
        return new(intents, tools, confirm);
    }
}

using ToDo.Entities;

namespace ToDo.Domain;

public sealed record AgentToolDescriptor(
    string ToolName,
    string NativeFunctionName,
    string DisplayName,
    string Description,
    string ArgumentsExampleJson,
    string ParametersJsonSchema,
    bool RequiresProject,
    AgentRiskLevel RiskLevel,
    AgentToolReviewMode MinimumReviewMode,
    bool SupportsApproval);

public interface IAgentToolCatalog
{
    IReadOnlyList<AgentToolDescriptor> GetAll();
    AgentToolDescriptor? Get(string toolName);
}

public sealed class AgentToolCatalog : IAgentToolCatalog
{
    private static readonly IReadOnlyList<AgentToolDescriptor> Tools =
    [
        new("web.search", "web_search", "联网搜索公开资料", "搜索公开网页，返回来源链接和摘要。查询词会发送至搜索供应商，禁止包含项目原文、个人信息、密钥。", """{"query":"公开技术资料","maxResults":3}""", """{"type":"object","properties":{"query":{"type":"string","minLength":2,"maxLength":300},"maxResults":{"type":"integer","minimum":1,"maximum":5}},"required":["query"],"additionalProperties":false}""", true, AgentRiskLevel.Low, AgentToolReviewMode.Direct, false),
        new("task.add_comment", "task_add_comment", "添加任务评论", "向指定任务写入 Agent 评论。", """{"taskId":1,"content":"评论"}""", """{"type":"object","properties":{"taskId":{"type":"integer","description":"任务ID；关联任务的 Session 可省略"},"content":{"type":"string","description":"要写入的评论正文"}},"required":["content"],"additionalProperties":false}""", true, AgentRiskLevel.Low, AgentToolReviewMode.Direct, true),
        new("task.update", "task_update", "修改任务", "修改任务状态、负责人、截止时间或描述。", """{"taskId":1,"status":"InProgress","assigneeId":2,"deadline":"2026-08-01","description":"新描述"}""", """{"type":"object","properties":{"taskId":{"type":"integer"},"status":{"type":"string","enum":["NotStarted","InProgress","PendingConfirmation","Completed","Cancelled"]},"assigneeId":{"type":"integer"},"deadline":{"type":"string","description":"yyyy-MM-dd"},"description":{"type":"string"}},"additionalProperties":false}""", true, AgentRiskLevel.High, AgentToolReviewMode.HumanApproval, false),
        new("task.create", "task_create", "创建任务", "在当前项目中创建任务。", """{"title":"任务","description":"说明","assigneeId":2,"deadline":"2026-08-01","priority":"Medium"}""", """{"type":"object","properties":{"projectId":{"type":"integer"},"title":{"type":"string"},"description":{"type":"string"},"assigneeId":{"type":"integer"},"deadline":{"type":"string","description":"yyyy-MM-dd"},"priority":{"type":"string","enum":["High","Medium","Low"]}},"required":["title"],"additionalProperties":false}""", true, AgentRiskLevel.Medium, AgentToolReviewMode.AiReview, true),
        new("project.document.write", "project_document_write", "写入项目资料", "按资料分类创建或更新 Agent 产物。", """{"category":"任务与计划","fileName":"计划.md","content":"内容","description":"说明"}""", """{"type":"object","properties":{"projectId":{"type":"integer"},"category":{"type":"string","description":"资料分类名称"},"fileName":{"type":"string"},"content":{"type":"string"},"description":{"type":"string"}},"required":["category","content"],"additionalProperties":false}""", true, AgentRiskLevel.High, AgentToolReviewMode.HumanApproval, false),
        new("project.update", "project_update", "修改项目", "修改当前项目的名称、说明、目标或状态。projectId 只能填写数字内部 ID；已有项目 Session 时应省略 projectId，不得填写项目名称。", """{"name":"项目名","description":"项目说明","requirements":"项目目标","status":"Active"}""", """{"type":"object","properties":{"projectId":{"type":"integer","description":"项目数字内部 ID；当前 Session 已关联项目时请省略"},"name":{"type":"string"},"description":{"type":"string"},"requirements":{"type":"string"},"status":{"type":"string","enum":["Active","Archived"]}},"additionalProperties":false}""", true, AgentRiskLevel.High, AgentToolReviewMode.HumanApproval, false),
        new("meeting.action.create", "meeting_action_create", "创建会议行动项", "为当前项目的会议创建行动项。", """{"meetingId":1,"content":"行动项","assigneeId":2,"deadline":"2026-08-01"}""", """{"type":"object","properties":{"meetingId":{"type":"integer"},"content":{"type":"string"},"assigneeId":{"type":"integer"},"assigneeText":{"type":"string"},"deadline":{"type":"string","description":"yyyy-MM-dd"},"matchedTaskId":{"type":"integer"}},"required":["meetingId","content"],"additionalProperties":false}""", true, AgentRiskLevel.Medium, AgentToolReviewMode.AiReview, true),
        new("report.create", "report_create", "生成报告", "创建日报、周报或月报。", """{"reportType":1,"reportDate":"2026-07-24","title":"日报","content":"日报内容"}""", """{"type":"object","properties":{"projectId":{"type":"integer"},"reportType":{"type":"integer","enum":[1,2,3]},"reportDate":{"type":"string","description":"yyyy-MM-dd"},"title":{"type":"string"},"content":{"type":"string"}},"required":["title","content"],"additionalProperties":false}""", true, AgentRiskLevel.Medium, AgentToolReviewMode.AiReview, true)
    ];

    public IReadOnlyList<AgentToolDescriptor> GetAll() => Tools;

    public AgentToolDescriptor? Get(string toolName)
    {
        return Tools.FirstOrDefault(item =>
            string.Equals(item.ToolName, toolName?.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

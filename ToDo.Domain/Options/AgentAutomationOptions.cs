namespace ToDo.Domain.Options;

/// <summary>Agent 后台队列的并发与轮询配置。</summary>
public sealed class AgentAutomationOptions
{
    /// <summary>单个应用实例同时处理的 Agent 工作数，运行时限制为 1-16。</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>队列为空时的轮询间隔，运行时限制为 100-30000 毫秒。</summary>
    public int PollIntervalMilliseconds { get; set; } = 1000;

    /// <summary>持续空闲后的最大轮询间隔，运行时限制为最小间隔到 60000 毫秒。</summary>
    public int MaxPollIntervalMilliseconds { get; set; } = 15000;

    /// <summary>恢复中断任务、补偿派单等维护操作的间隔，运行时限制为 1-300 秒。</summary>
    public int MaintenanceIntervalSeconds { get; set; } = 5;
}

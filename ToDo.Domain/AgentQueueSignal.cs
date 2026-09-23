namespace ToDo.Domain;

/// <summary>同一应用实例内的 Agent 队列唤醒信号；数据库仍是唯一任务事实源。</summary>
public sealed class AgentQueueSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1024);

    public void Notify()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => _signal.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _signal.Dispose();
}

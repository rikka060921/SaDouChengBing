using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ToDo.Domain;

/// <summary>零依赖的标准 Activity/Meter 埋点；生产环境可由 OpenTelemetry Collector 直接订阅。</summary>
public sealed class AgentTelemetry : IDisposable
{
    public const string SourceName = "SaDouChengBing.AgentAutomation";
    public ActivitySource Activities { get; } = new(SourceName, "1.0.0");
    private readonly Meter _meter = new(SourceName, "1.0.0");
    private readonly Counter<long> _queued;
    private readonly Counter<long> _attemptSucceeded;
    private readonly Counter<long> _attemptFailed;
    private readonly Histogram<double> _queueWaitMs;
    private readonly Histogram<double> _executionMs;

    public AgentTelemetry()
    {
        _queued = _meter.CreateCounter<long>("agent.jobs.queued", unit: "job");
        _attemptSucceeded = _meter.CreateCounter<long>("agent.attempts.succeeded", unit: "attempt");
        _attemptFailed = _meter.CreateCounter<long>("agent.attempts.failed", unit: "attempt");
        _queueWaitMs = _meter.CreateHistogram<double>("agent.queue.wait", unit: "ms");
        _executionMs = _meter.CreateHistogram<double>("agent.execution.duration", unit: "ms");
    }

    public void RecordQueued(string queue) => _queued.Add(1, new KeyValuePair<string, object?>("queue", queue));

    public void RecordExecution(string queue, bool succeeded, double? queueWaitMilliseconds, double executionMilliseconds)
    {
        var queueTag = new KeyValuePair<string, object?>("queue", queue);
        (succeeded ? _attemptSucceeded : _attemptFailed).Add(1, queueTag);
        if (queueWaitMilliseconds.HasValue)
            _queueWaitMs.Record(Math.Max(0, queueWaitMilliseconds.Value), queueTag);
        _executionMs.Record(Math.Max(0, executionMilliseconds), queueTag);
    }

    public Activity? StartConsumer(string operation, string queue, long jobId, int? projectId, int? taskId)
    {
        var activity = Activities.StartActivity(operation, ActivityKind.Consumer);
        activity?.SetTag("messaging.system", "database");
        activity?.SetTag("messaging.destination.name", queue);
        activity?.SetTag("agent.job.id", jobId);
        activity?.SetTag("project.id", projectId);
        activity?.SetTag("task.id", taskId);
        return activity;
    }

    public void Dispose()
    {
        Activities.Dispose();
        _meter.Dispose();
    }
}

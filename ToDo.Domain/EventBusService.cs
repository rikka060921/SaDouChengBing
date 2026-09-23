using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public interface IEventBus
{
    Task<EventBusMessage> PublishAsync(string eventType, object payload, string aggregateType = "", string aggregateId = "", CancellationToken cancellationToken = default);
    Task<List<EventBusMessage>> GetRecentAsync(int take = 100, CancellationToken cancellationToken = default);
}

public class EventBusService : IEventBus
{
    private readonly ApplicationDbContext _context;

    public EventBusService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<EventBusMessage> PublishAsync(string eventType, object payload, string aggregateType = "", string aggregateId = "", CancellationToken cancellationToken = default)
    {
        var message = new EventBusMessage
        {
            EventType = eventType,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            PayloadJson = JsonSerializer.Serialize(payload),
            Status = EventBusMessageStatus.Pending
        };
        _context.EventBusMessages.Add(message);
        await _context.SaveChangesAsync(cancellationToken);
        return message;
    }

    public Task<List<EventBusMessage>> GetRecentAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        return _context.EventBusMessages.AsNoTracking().OrderByDescending(e => e.CreatedAt).Take(take).ToListAsync(cancellationToken);
    }

}

public class EventBusDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EventBusDispatcher> _logger;

    public EventBusDispatcher(IServiceScopeFactory scopeFactory, ILogger<EventBusDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var automation = scope.ServiceProvider.GetRequiredService<AgentEventAutomationService>();
                await automation.RecoverStaleAsync(stoppingToken);
                didWork = await automation.DispatchPendingEventsAsync(stoppingToken) > 0;
                await automation.ResumeResolvedApprovalsAsync(stoppingToken);
                var executionId = await automation.ClaimNextExecutionAsync(stoppingToken);
                if (executionId.HasValue)
                {
                    await automation.ProcessExecutionAsync(executionId.Value, stoppingToken);
                    didWork = true;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "事件总线处理失败");
            }

            try
            {
                // 有积压时快速排空；空闲时降低数据库轮询频率。
                await Task.Delay(didWork ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Agents.Manage;

[Authorize(Roles = "systemAdmin")]
public sealed class MetricsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    public MetricsModel(ApplicationDbContext context) => _context = context;

    public DateTime From { get; private set; }
    public int Days { get; private set; } = 30;
    public int? ProjectId { get; private set; }
    public int? AgentVersion { get; private set; }
    public List<Project> Projects { get; private set; } = [];
    public List<int> Versions { get; private set; } = [];
    public List<AgentMetricRow> Rows { get; private set; } = [];
    public int TotalSessions => Rows.Sum(item => item.SessionCount);
    public int FailedSessions => Rows.Sum(item => item.FailedCount);
    public int TotalTokens => Rows.Sum(item => item.TotalTokens);
    public decimal EstimatedCost => Rows.Sum(item => item.EstimatedCost);
    public int PendingDeliveries => Rows.Sum(item => item.PendingDeliveries);
    public int ReviewedDeliveries => Rows.Sum(item => item.AcceptedDeliveries + item.RejectedDeliveries);
    public int AutomaticallyAcceptedDeliveries => Rows.Sum(item => item.AutomaticallyAcceptedDeliveries);
    public double HumanAcceptanceRate => ReviewedDeliveries == 0
        ? 0
        : Rows.Sum(item => item.AcceptedDeliveries) * 100d / ReviewedDeliveries;
    public decimal CostPerAcceptedDelivery => Rows.Sum(item => item.AcceptedDeliveries + item.AutomaticallyAcceptedDeliveries) == 0
        ? 0 : EstimatedCost / Rows.Sum(item => item.AcceptedDeliveries + item.AutomaticallyAcceptedDeliveries);
    public int ConfirmedDispatches { get; private set; }
    public int OverriddenDispatches { get; private set; }
    public double DispatchOverrideRate => ConfirmedDispatches == 0 ? 0 : OverriddenDispatches * 100d / ConfirmedDispatches;

    public async Task OnGetAsync(int days = 30, int? projectId = null, int? agentVersion = null)
    {
        days = Math.Clamp(days, 1, 365);
        Days = days;
        ProjectId = projectId;
        AgentVersion = agentVersion;
        From = AppTime.Now.AddDays(-days);
        Projects = await _context.Project.AsNoTracking().Where(item => !item.IsDeleted).OrderBy(item => item.Name).ToListAsync();
        Versions = await _context.AiSessions.AsNoTracking().Where(item => item.StartedAt >= From)
            .Select(item => item.AgentVersion).Distinct().OrderByDescending(item => item).ToListAsync();
        var sessionQuery = _context.AiSessions.AsNoTracking().Where(item => item.StartedAt >= From);
        if (projectId.HasValue) sessionQuery = sessionQuery.Where(item => item.ProjectId == projectId.Value);
        if (agentVersion.HasValue) sessionQuery = sessionQuery.Where(item => item.AgentVersion == agentVersion.Value);
        Rows = await sessionQuery
            .GroupBy(item => new { item.AgentKey, item.AgentVersion })
            .Select(group => new AgentMetricRow
            {
                AgentKey = group.Key.AgentKey,
                AgentVersion = group.Key.AgentVersion,
                SessionCount = group.Count(),
                CompletedCount = group.Count(item => item.Status == AiSessionStatus.WaitingHuman || item.Status == AiSessionStatus.Succeeded),
                FailedCount = group.Count(item => item.Status == AiSessionStatus.Failed),
                CancelledCount = group.Count(item => item.Status == AiSessionStatus.Cancelled),
                TotalTurns = group.Sum(item => item.TurnCount),
                TotalTokens = group.Sum(item => (item.InputTokens ?? 0) + (item.OutputTokens ?? 0)),
                AverageDurationMilliseconds = group.Average(item => (double?)item.DurationMilliseconds) ?? 0,
                EstimatedCost = group.Sum(item => item.EstimatedCost ?? 0),
                LastRunAt = group.Max(item => item.LastActivityAt)
            })
            .OrderByDescending(item => item.SessionCount)
            .ToListAsync();

        var agents = await _context.AgentDefinitions.AsNoTracking()
            .Select(item => new { item.Id, item.AgentKey, item.Name })
            .ToListAsync();
        var names = agents.ToDictionary(item => item.AgentKey, item => item.Name);
        foreach (var row in Rows) row.AgentName = names.GetValueOrDefault(row.AgentKey, row.AgentKey);

        var deliveryQuery = _context.AgentDeliveryReceipts.AsNoTracking().Where(item => item.CreatedAt >= From);
        if (projectId.HasValue) deliveryQuery = deliveryQuery.Where(item => item.ProjectId == projectId.Value);
        if (agentVersion.HasValue) deliveryQuery = deliveryQuery.Where(item => item.AgentVersion == agentVersion.Value);
        var deliveries = await deliveryQuery
            .GroupBy(item => new { item.AgentDefinitionId, item.AgentVersion })
            .Select(group => new
            {
                group.Key.AgentDefinitionId,
                group.Key.AgentVersion,
                Submitted = group.Count(),
                Pending = group.Count(item => item.AcceptanceStatus == AgentDeliveryAcceptanceStatus.PendingReview),
                Accepted = group.Count(item => item.AcceptanceStatus == AgentDeliveryAcceptanceStatus.Accepted),
                AutoAccepted = group.Count(item => item.AcceptanceStatus == AgentDeliveryAcceptanceStatus.AutomaticallyAccepted),
                Rejected = group.Count(item => item.AcceptanceStatus == AgentDeliveryAcceptanceStatus.Rejected),
                LastDeliveryAt = group.Max(item => item.CreatedAt)
            })
            .ToListAsync();
        var rowsByKey = Rows.ToDictionary(item => (item.AgentKey, item.AgentVersion));
        var keysById = agents.ToDictionary(item => item.Id, item => item.AgentKey);
        foreach (var delivery in deliveries)
        {
            if (!keysById.TryGetValue(delivery.AgentDefinitionId, out var key))
                continue;
            if (!rowsByKey.TryGetValue((key, delivery.AgentVersion), out var row))
            {
                row = new AgentMetricRow
                {
                    AgentKey = key,
                    AgentVersion = delivery.AgentVersion,
                    AgentName = names.GetValueOrDefault(key, key),
                    LastRunAt = delivery.LastDeliveryAt
                };
                Rows.Add(row);
                rowsByKey.Add((key, delivery.AgentVersion), row);
            }
            row.SubmittedDeliveries = delivery.Submitted;
            row.PendingDeliveries = delivery.Pending;
            row.AcceptedDeliveries = delivery.Accepted;
            row.AutomaticallyAcceptedDeliveries = delivery.AutoAccepted;
            row.RejectedDeliveries = delivery.Rejected;
        }
        Rows = Rows.OrderByDescending(item => item.SessionCount)
            .ThenByDescending(item => item.SubmittedDeliveries)
            .ThenBy(item => item.AgentName)
            .ToList();

        var dispatchQuery = _context.AgentDispatchDecisions.AsNoTracking()
            .Where(item => item.CreatedAt >= From && item.Status == AgentDispatchDecisionStatus.Confirmed);
        if (projectId.HasValue) dispatchQuery = dispatchQuery.Where(item => item.ProjectId == projectId.Value);
        ConfirmedDispatches = await dispatchQuery.CountAsync();
        OverriddenDispatches = await dispatchQuery.CountAsync(item => item.RecommendedAgentDefinitionId.HasValue
            && item.SelectedAgentDefinitionId.HasValue
            && item.RecommendedAgentDefinitionId != item.SelectedAgentDefinitionId);
    }
}

public sealed class AgentMetricRow
{
    public string AgentKey { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public int AgentVersion { get; set; }
    public int SessionCount { get; set; }
    public int CompletedCount { get; set; }
    public int FailedCount { get; set; }
    public int CancelledCount { get; set; }
    public int TotalTurns { get; set; }
    public int TotalTokens { get; set; }
    public double AverageDurationMilliseconds { get; set; }
    public decimal EstimatedCost { get; set; }
    public DateTime LastRunAt { get; set; }
    public double CompletionRate => SessionCount == 0 ? 0 : CompletedCount * 100d / SessionCount;
    public int SubmittedDeliveries { get; set; }
    public int PendingDeliveries { get; set; }
    public int AcceptedDeliveries { get; set; }
    public int AutomaticallyAcceptedDeliveries { get; set; }
    public int RejectedDeliveries { get; set; }
    public double HumanAcceptanceRate => AcceptedDeliveries + RejectedDeliveries == 0
        ? 0
        : AcceptedDeliveries * 100d / (AcceptedDeliveries + RejectedDeliveries);
    public decimal CostPerAcceptedDelivery => AcceptedDeliveries + AutomaticallyAcceptedDeliveries == 0
        ? 0 : EstimatedCost / (AcceptedDeliveries + AutomaticallyAcceptedDeliveries);
    public double ReworkRate => AcceptedDeliveries + RejectedDeliveries == 0
        ? 0 : RejectedDeliveries * 100d / (AcceptedDeliveries + RejectedDeliveries);
}

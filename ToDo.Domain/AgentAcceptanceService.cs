using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public sealed class AgentAcceptanceService
{
    private const string StartMarker = "<acceptance-result>";
    private const string EndMarker = "</acceptance-result>";
    private readonly ApplicationDbContext _context;

    public AgentAcceptanceService(ApplicationDbContext context) => _context = context;

    public async Task CompleteAsync(int sessionId, string response, CancellationToken cancellationToken = default)
    {
        var recommendation = await _context.AgentAcceptanceRecommendations
            .FirstOrDefaultAsync(item => item.AiSessionId == sessionId, cancellationToken);
        if (recommendation == null || recommendation.Status != AgentAcceptanceRecommendationStatus.Pending) return;

        recommendation.RawResponse = response;
        recommendation.CompletedAt = AppTime.Now;
        recommendation.Status = AgentAcceptanceRecommendationStatus.Completed;
        recommendation.ErrorMessage = string.Empty;

        if (TryParse(response, out var parsed))
        {
            recommendation.Verdict = ParseVerdict(parsed!.Verdict);
            recommendation.Confidence = Math.Clamp(parsed.Confidence, 0, 1);
            recommendation.Summary = Truncate(parsed.Summary, 8000);
            recommendation.EvidenceCoverageJson = SerializeList(parsed.EvidenceCoverage);
            recommendation.MissingItemsJson = SerializeList(parsed.MissingItems);
            recommendation.RisksJson = SerializeList(parsed.Risks);
            recommendation.HumanReviewQuestionsJson = SerializeList(parsed.HumanReviewQuestions);
            recommendation.ParseMode = "structured";
            return;
        }

        recommendation.Verdict = response.Contains("建议驳回", StringComparison.Ordinal)
            ? AgentAcceptanceVerdict.RecommendReject
            : response.Contains("建议通过", StringComparison.Ordinal)
                ? AgentAcceptanceVerdict.RecommendAccept
                : AgentAcceptanceVerdict.NeedsHumanReview;
        recommendation.Confidence = 0.35;
        recommendation.Summary = Truncate(response, 8000);
        recommendation.ParseMode = "fallback";
    }

    public async Task FailAsync(int sessionId, string error, CancellationToken cancellationToken = default)
    {
        var recommendation = await _context.AgentAcceptanceRecommendations
            .FirstOrDefaultAsync(item => item.AiSessionId == sessionId, cancellationToken);
        if (recommendation == null || recommendation.Status != AgentAcceptanceRecommendationStatus.Pending) return;
        recommendation.Status = AgentAcceptanceRecommendationStatus.Failed;
        recommendation.ErrorMessage = Truncate(error, 2000);
        recommendation.CompletedAt = AppTime.Now;
        recommendation.ParseMode = "failed";
    }

    public static IReadOnlyList<string> ParseItems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static bool TryParse(string response, out AcceptancePayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(response)) return false;
        var start = response.IndexOf(StartMarker, StringComparison.OrdinalIgnoreCase);
        var end = response.IndexOf(EndMarker, StringComparison.OrdinalIgnoreCase);
        string json;
        if (start >= 0 && end > start)
            json = response[(start + StartMarker.Length)..end].Trim();
        else
        {
            var firstBrace = response.IndexOf('{');
            var lastBrace = response.LastIndexOf('}');
            if (firstBrace < 0 || lastBrace <= firstBrace) return false;
            json = response[firstBrace..(lastBrace + 1)];
        }
        try
        {
            payload = JsonSerializer.Deserialize<AcceptancePayload>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return payload != null && !string.IsNullOrWhiteSpace(payload.Verdict);
        }
        catch (JsonException) { return false; }
    }

    private static AgentAcceptanceVerdict ParseVerdict(string verdict)
        => verdict.Trim().ToLowerInvariant() switch
        {
            "accept" or "recommendaccept" or "建议通过" => AgentAcceptanceVerdict.RecommendAccept,
            "reject" or "recommendreject" or "建议驳回" => AgentAcceptanceVerdict.RecommendReject,
            _ => AgentAcceptanceVerdict.NeedsHumanReview
        };

    private static string SerializeList(List<string>? values)
        => JsonSerializer.Serialize((values ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => Truncate(item.Trim(), 1000)).Take(50));

    private static string Truncate(string? value, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private sealed class AcceptancePayload
    {
        public string Verdict { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string Summary { get; set; } = string.Empty;
        public List<string> EvidenceCoverage { get; set; } = [];
        public List<string> MissingItems { get; set; } = [];
        public List<string> Risks { get; set; } = [];
        public List<string> HumanReviewQuestions { get; set; } = [];
    }
}

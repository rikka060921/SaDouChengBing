using System.Text.Json;
using System.Text.RegularExpressions;

namespace ToDo.Domain;

public sealed record RedBlueJudgeResult(string Decision, string Winner, bool IsStructured);

/// <summary>
/// 解析红蓝裁判的结构化结论。结构化标记缺失时只接受明确的“胜方：...”文本，
/// 不再因为正文中提到“红方”或“蓝方”就误判胜方。
/// </summary>
public static partial class RedBlueJudgeParser
{
    public const string ResultStart = "<red-blue-result>";
    public const string ResultEnd = "</red-blue-result>";

    public static RedBlueJudgeResult Parse(string? response)
    {
        var raw = response?.Trim() ?? string.Empty;
        var start = raw.LastIndexOf(ResultStart, StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            var end = raw.IndexOf(ResultEnd, start + ResultStart.Length, StringComparison.OrdinalIgnoreCase);
            if (end >= 0)
            {
                var json = raw[(start + ResultStart.Length)..end].Trim();
                var decision = (raw[..start] + raw[(end + ResultEnd.Length)..]).Trim();
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("winner", out var winnerElement))
                    {
                        var winner = NormalizeWinner(winnerElement.GetString());
                        if (winner != null)
                            return new RedBlueJudgeResult(decision, winner, true);
                    }
                }
                catch (JsonException)
                {
                    // 继续使用明确文本兜底，无法解析时安全地返回平局。
                }
            }
        }

        var match = ExplicitWinnerRegex().Match(raw);
        var fallbackWinner = match.Success ? NormalizeWinner(match.Groups[1].Value) : null;
        return new RedBlueJudgeResult(raw, fallbackWinner ?? "平局", false);
    }

    public static string ResolveOverallWinner(IEnumerable<string> winners)
    {
        var normalized = winners.Select(NormalizeWinner).Where(item => item != null).ToList();
        var red = normalized.Count(item => item == "红方");
        var blue = normalized.Count(item => item == "蓝方");
        return red == blue ? "平局" : red > blue ? "红方" : "蓝方";
    }

    private static string? NormalizeWinner(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "red" or "红" or "红方" => "红方",
            "blue" or "蓝" or "蓝方" => "蓝方",
            "draw" or "tie" or "平" or "平局" => "平局",
            _ => null
        };
    }

    [GeneratedRegex(@"(?:最终胜方|胜方|裁判结论)\s*[：:]\s*(红方|蓝方|平局)(?=\s|$|[，。；;])", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitWinnerRegex();
}

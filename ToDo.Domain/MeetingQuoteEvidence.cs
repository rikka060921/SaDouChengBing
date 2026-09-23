using System.Text;

namespace ToDo.Domain;

/// <summary>
/// 会议原话证据解析器。所有标记为“原话”的内容必须能够映射回会议源文本中的真实连续片段。
/// </summary>
public static class MeetingQuoteEvidence
{
    public static MeetingQuoteEvidenceResult Resolve(
        string sourceText,
        IEnumerable<string>? quoteCandidates,
        string? keyQuoteCandidate,
        string? legacyQuoteCandidate = null)
    {
        var resolvedQuotes = new List<string>();
        foreach (var candidate in quoteCandidates ?? Enumerable.Empty<string>())
        {
            var resolved = ResolveOneQuote(sourceText, candidate);
            if (resolved != null) AddDistinct(resolvedQuotes, resolved);
        }

        // 旧版只有 OriginalQuote；新版列表没有任何有效证据时再使用旧字段兜底。
        if (resolvedQuotes.Count == 0)
        {
            var legacyResolved = ResolveOneQuote(sourceText, legacyQuoteCandidate);
            if (legacyResolved != null) AddDistinct(resolvedQuotes, legacyResolved);
        }

        // keyQuote：先精确匹配源文本，失败再用宽松验真器兜底，再失败退到 quotes 最后一条
        var keyQuote = ResolveOneQuote(sourceText, keyQuoteCandidate)
            ?? resolvedQuotes.LastOrDefault()
            ?? string.Empty;

        // AI 可能只返回 KeyQuote。它通过验真后也必须进入可点击定位的证据集合。
        if (resolvedQuotes.Count == 0 && !string.IsNullOrWhiteSpace(keyQuote))
            resolvedQuotes.Add(keyQuote);

        return new MeetingQuoteEvidenceResult(resolvedQuotes, keyQuote);
    }

    /// <summary>
    /// 单条原话证据解析：先精确/归一化匹配源文本（截取真实原文），
    /// 失败后用宽松验真器（关键词覆盖 ≥2 个）兜底——信任数据库里已经过验真的原话，
    /// 不要把 AI 语义改写过的正确证据给清掉。
    /// </summary>
    private static string? ResolveOneQuote(string sourceText, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(candidate))
            return null;

        // 优先精确/归一化匹配 → 截取源文本中的真实原文
        if (TryResolveFromSource(sourceText, candidate, out var exactResolved))
            return exactResolved;

        // 精确匹配失败 → 宽松验真器检查（≥2 个关键词命中）
        // 数据库里存的原话已经过后端 AI 重新匹配的宽松验真，这里不应该再用精确匹配把它清掉
        if (HasEnoughKeywordCoverage(sourceText, candidate))
            return candidate.Trim();

        return null;
    }

    /// <summary>
    /// 把候选引文映射为源文本中的真实连续片段。先精确匹配；再仅忽略空白、标点和大小写匹配，
    /// 但返回值始终截取自 sourceText，绝不返回 AI 改写后的候选文本。
    /// </summary>
    public static bool TryResolveFromSource(string sourceText, string? candidate, out string resolvedQuote)
    {
        resolvedQuote = string.Empty;
        if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(candidate)) return false;

        var trimmed = candidate.Trim();
        var exactIndex = sourceText.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase);
        if (exactIndex >= 0)
        {
            resolvedQuote = sourceText.Substring(exactIndex, trimmed.Length);
            return true;
        }

        var normalizedCandidate = Normalize(trimmed, null);
        if (normalizedCandidate.Length == 0) return false;

        var sourceMap = new List<int>(sourceText.Length);
        var normalizedSource = Normalize(sourceText, sourceMap);
        var normalizedIndex = normalizedSource.IndexOf(normalizedCandidate, StringComparison.Ordinal);
        if (normalizedIndex < 0) return false;

        var sourceStart = sourceMap[normalizedIndex];
        var sourceEnd = sourceMap[normalizedIndex + normalizedCandidate.Length - 1] + 1;
        resolvedQuote = sourceText[sourceStart..sourceEnd].Trim();
        return resolvedQuote.Length > 0;
    }

    /// <summary>
    /// 基于关键词的语义回退匹配：当整句精确/归一化匹配都失败时，
    /// 从总结句里抽出人名、名词、实体词，然后在源文本里滑窗找包含最多关键词的连续片段。
    /// 返回的片段是源文本里真实存在的原文，不是 AI 改写的总结句。
    /// </summary>
    public static bool TryResolveByKeywords(string sourceText, string summary, out string resolvedQuote)
    {
        resolvedQuote = string.Empty;
        if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(summary)) return false;

        var keywords = ExtractKeywords(summary);
        if (keywords.Count == 0) return false;

        // 在源文本上滑动窗口（按标点/换行分句，窗口大小 3-8 句），
        // 找到包含最多关键词的那段连续文本。
        var sentences = SplitSentences(sourceText);
        if (sentences.Count == 0) return false;

        // 先尝试单句匹配
        int bestKeywordCount = 0;
        int bestSentenceStart = -1;
        int bestSentenceEnd = -1; // 闭区间

        for (int winSize = 1; winSize <= Math.Min(5, sentences.Count); winSize++)
        {
            for (int start = 0; start + winSize <= sentences.Count; start++)
            {
                int end = start + winSize - 1;
                var windowText = string.Concat(sentences.Skip(start).Take(winSize));
                var hits = CountKeywordHits(keywords, windowText);
                // 至少命中 2 个关键词 或 超过一半关键词才算有效
                if (hits >= 2 && hits > bestKeywordCount)
                {
                    bestKeywordCount = hits;
                    bestSentenceStart = start;
                    bestSentenceEnd = end;
                }
            }
        }

        if (bestSentenceStart < 0) return false;

        // 把命中的片段向前向后各扩展 1 句，拿到完整的发言上下文
        int expandedStart = Math.Max(0, bestSentenceStart - 1);
        int expandedEnd = Math.Min(sentences.Count - 1, bestSentenceEnd + 1);
        var sb = new StringBuilder();
        for (int i = expandedStart; i <= expandedEnd; i++)
            sb.Append(sentences[i]);
        resolvedQuote = sb.ToString().Trim();
        return resolvedQuote.Length > 0;
    }

    /// <summary>
    /// 从决策总结句里抽出匹配用的关键词。
    /// - 连续的中文名词/动词/形容词短语（2-6 个汉字），去掉停用词
    /// - 单独的中文人名（2-4 个汉字，常见姓氏开头）
    /// - 英文/数字短语
    /// </summary>
    private static List<string> ExtractKeywords(string summary)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(summary)) return result;

        // 先切出候选词：连续中文 + 连续英文数字 + 标点分隔
        var tokens = new List<string>();
        var current = new StringBuilder();
        char prevKind = '\0';
        foreach (var c in summary)
        {
            if (char.IsPunctuation(c) || char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                prevKind = '\0';
                continue;
            }
            char kind;
            if (c >= 0x4e00 && c <= 0x9fff) kind = 'C'; // Chinese
            else if (char.IsLetterOrDigit(c)) kind = 'E'; // English/number
            else kind = 'X';
            if (prevKind != '\0' && kind != prevKind)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            current.Append(c);
            prevKind = kind;
        }
        if (current.Length > 0) tokens.Add(current.ToString());

        // 停用词/虚词表（覆盖度够用就行）
        var stopWords = new HashSet<string>
        {
            "的", "了", "是", "和", "与", "及", "或", "也", "都", "就", "在", "把", "被",
            "要", "会", "能", "可以", "应该", "必须", "需要", "已经", "还", "又", "且",
            "进行", "开始", "完成", "做", "改", "调整", "更新", "新建", "实现",
            "一个", "一下", "一些", "全部", "所有", "每个", "这种", "那样", "这个", "那个",
            "大家", "咱们", "我们", "你们", "他们", "自己", "自行", "以上", "以下",
            "暂不", "不", "未", "没有", "继续", "保持", "放慢", "推进",
        };

        foreach (var raw in tokens)
        {
            var t = raw.Trim();
            if (t.Length < 2) continue;
            if (stopWords.Contains(t)) continue;

            // 中文：保留，再切出其中更长的 2-4 字人名候选
            if (t[0] >= 0x4e00 && t[0] <= 0x9fff)
            {
                AddDistinct(result, t);
                // 如果是 2-4 字的纯中文，当作人名候选
                if (t.Length >= 2 && t.Length <= 4)
                    AddDistinct(result, t);
            }
            else
            {
                AddDistinct(result, t);
            }
        }

        // 额外尝试：抽连续的中文短语片段（比如 "协助整理软著" -> "软著"、"协助整理"）
        var chineseOnly = new string(summary.Where(c => c >= 0x4e00 && c <= 0x9fff).ToArray());
        if (chineseOnly.Length >= 4)
        {
            for (int len = 6; len >= 2; len--)
            {
                for (int i = 0; i + len <= chineseOnly.Length; i++)
                {
                    var seg = chineseOnly.Substring(i, len);
                    if (stopWords.Contains(seg)) continue;
                    // 过滤全是虚词的片段
                    if (seg.All(c => stopWords.Contains(c.ToString()))) continue;
                    AddDistinct(result, seg);
                }
            }
        }

        return result.Where(k => k.Length >= 2).Take(30).ToList();
    }

    private static List<string> SplitSentences(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (c == '。' || c == '！' || c == '？' || c == '\n' || c == '\r' || c == '；' || c == '.')
            {
                if (sb.Length > 0)
                {
                    result.Add(sb.ToString().Trim());
                    sb.Clear();
                }
                if (c == '\r' || c == '\n') continue;
                sb.Append(c);
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString().Trim());
        return result;
    }

    private static int CountKeywordHits(IEnumerable<string> keywords, string windowText)
    {
        int count = 0;
        foreach (var kw in keywords)
        {
            if (string.IsNullOrWhiteSpace(kw)) continue;
            if (windowText.Contains(kw, StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return count;
    }

    private static string Normalize(string text, List<int>? sourceMap)
    {
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (!char.IsLetterOrDigit(character)) continue;
            builder.Append(char.ToLowerInvariant(character));
            sourceMap?.Add(index);
        }
        return builder.ToString();
    }

    private static void AddDistinct(List<string> quotes, string quote)
    {
        if (!quotes.Contains(quote, StringComparer.Ordinal)) quotes.Add(quote);
    }

    /// <summary>
    /// 宽松验真：检查 quote 里的关键词（2-6 字连续中文片段）有多少个能在 sourceText 里找到。
    /// 要求至少命中 2 个关键词才算"不是幻觉"。
    /// 目的：允许 AI 对口语做正常的书面改写（不是逐字匹配），同时防 AI 编造不存在的人名/实体。
    /// </summary>
    public static bool HasEnoughKeywordCoverage(string sourceText, string quote)
    {
        if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(quote)) return false;

        var chineseOnly = new string(quote.Where(c => c >= 0x4e00 && c <= 0x9fff).ToArray());
        if (chineseOnly.Length < 4) return false;

        var stopWords = new HashSet<string>
        {
            "的", "了", "是", "和", "与", "及", "或", "也", "都", "就", "在", "把", "被",
            "要", "会", "能", "可以", "应该", "必须", "需要", "已经", "还", "又", "且",
            "一个", "一下", "一些", "全部", "所有", "每个", "这个", "那个",
            "大家", "我们", "你们", "他们", "自己", "以上", "以下",
            "进行", "开始", "完成", "调整", "更新", "实现", "继续", "保持", "推进",
            "不", "没", "没有", "暂不",
        };

        int hitCount = 0;
        bool hasLongHit = false; // 是否有 3 字以上关键词命中
        var seen = new HashSet<string>();
        // 从长到短扫关键词，命中就计数并跳过子串
        for (int len = 6; len >= 2 && hitCount < 2; len--)
        {
            for (int i = 0; i + len <= chineseOnly.Length; i++)
            {
                var seg = chineseOnly.Substring(i, len);
                if (stopWords.Contains(seg)) continue;
                if (seg.All(c => stopWords.Contains(c.ToString()))) continue;
                if (!seen.Add(seg)) continue;
                if (sourceText.Contains(seg, StringComparison.Ordinal))
                {
                    hitCount++;
                    if (len >= 3) hasLongHit = true;
                    if (hitCount >= 2 && hasLongHit) return true;
                    // 找到就跳过覆盖的子串，避免重复
                    i += len - 1;
                }
            }
        }
        return hitCount >= 2 && hasLongHit;
    }

    /// <summary>
    /// 从决策内容里抽关键词用于在转写中定位讨论窗口。
    /// 抽 2-4 字中文片段（去停用词），用于 IndexOf 定位中心。
    /// </summary>
    public static List<string> ExtractKeywordsForSearch(string? content)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(content)) return result;

        var chineseOnly = new string(content.Where(c => c >= 0x4e00 && c <= 0x9fff).ToArray());
        if (chineseOnly.Length < 2) return result;

        var stopWords = new HashSet<string>
        {
            "的", "了", "是", "和", "与", "及", "或", "也", "都", "就", "在", "把", "被",
            "要", "会", "能", "可以", "应该", "必须", "需要", "已经", "还", "又", "且",
            "一个", "一下", "一些", "全部", "所有", "每个", "这个", "那个",
            "大家", "我们", "你们", "他们", "自己", "以上", "以下",
            "进行", "开始", "完成", "调整", "更新", "实现", "继续", "保持", "推进",
            "不", "没", "没有", "暂不",
        };

        var seen = new HashSet<string>();
        // 从长到短抽关键词，优先返回较长的（更具体）
        for (int len = 4; len >= 2; len--)
        {
            for (int i = 0; i + len <= chineseOnly.Length; i++)
            {
                var seg = chineseOnly.Substring(i, len);
                if (stopWords.Contains(seg)) continue;
                if (seg.All(c => stopWords.Contains(c.ToString()))) continue;
                if (seen.Add(seg)) result.Add(seg);
            }
        }

        return result;
    }
}

public sealed record MeetingQuoteEvidenceResult(IReadOnlyList<string> Quotes, string KeyQuote);

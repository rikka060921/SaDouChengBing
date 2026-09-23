using ToDo.Domain;

namespace ToDo.Test;

public sealed class MeetingQuoteEvidenceTests
{
    [Fact]
    public void TryResolveFromSource_ExactMatch_ReturnsSourceText()
    {
        const string source = "赵冬：好，那就按方案 A 执行。";

        var success = MeetingQuoteEvidence.TryResolveFromSource(source, "那就按方案 A 执行", out var resolved);

        Assert.True(success);
        Assert.Equal("那就按方案 A 执行", resolved);
        Assert.Contains(resolved, source, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolveFromSource_PunctuationDiffers_ReturnsActualContinuousSourceSlice()
    {
        const string source = "赵冬：好，那就按方案 A 执行。";

        var success = MeetingQuoteEvidence.TryResolveFromSource(source, "赵冬: 好 那就按方案A执行", out var resolved);

        Assert.True(success);
        Assert.Equal("赵冬：好，那就按方案 A 执行", resolved);
        Assert.Contains(resolved, source, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_HallucinatedKeyQuote_FallsBackToLastVerifiedQuote()
    {
        const string source = "赵冬：这个问题怎么处理？\n乔宽：先灰度验证。\n赵冬：好，那就先灰度，下周验收。";

        var evidence = MeetingQuoteEvidence.Resolve(
            source,
            ["赵冬：这个问题怎么处理？", "赵冬：好，那就先灰度，下周验收。"],
            "决定明天正式上线");

        Assert.Equal(2, evidence.Quotes.Count);
        Assert.Equal("赵冬：好，那就先灰度，下周验收。", evidence.KeyQuote);
        Assert.DoesNotContain("决定明天正式上线", evidence.Quotes);
    }

    [Fact]
    public void Resolve_OnlyVerifiedKeyQuote_AddsItToClickableEvidence()
    {
        const string source = "赵冬：好，那就按方案 A 执行。";

        var evidence = MeetingQuoteEvidence.Resolve(source, [], "那就按方案 A 执行");

        Assert.Equal("那就按方案 A 执行", evidence.KeyQuote);
        Assert.Equal(["那就按方案 A 执行"], evidence.Quotes);
    }

    [Fact]
    public void Resolve_LegacyQuote_IsVerifiedAndUsedWhenNewFieldsAreEmpty()
    {
        const string source = "赵冬：好，那就按方案 A 执行。";

        var evidence = MeetingQuoteEvidence.Resolve(source, [], null, "那就按方案 A 执行");

        Assert.Equal("那就按方案 A 执行", evidence.KeyQuote);
        Assert.Equal(["那就按方案 A 执行"], evidence.Quotes);
    }

    [Fact]
    public void Resolve_NoCandidateCanBeLocated_ReturnsNoOriginalQuoteEvidence()
    {
        const string source = "会议只讨论了方案 A。";

        var evidence = MeetingQuoteEvidence.Resolve(
            source,
            ["会议决定采用方案 B"],
            "立即上线方案 B",
            "历史伪原话");

        Assert.Empty(evidence.Quotes);
        Assert.Empty(evidence.KeyQuote);
    }
}

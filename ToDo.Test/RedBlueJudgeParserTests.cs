using ToDo.Domain;

namespace ToDo.Test;

public class RedBlueJudgeParserTests
{
    [Theory]
    [InlineData("red", "红方")]
    [InlineData("blue", "蓝方")]
    [InlineData("draw", "平局")]
    public void Parse_StructuredWinner_ReturnsNormalizedWinner(string value, string expected)
    {
        var result = RedBlueJudgeParser.Parse($"裁判意见，同时分析红方和蓝方。\n<red-blue-result>{{\"winner\":\"{value}\"}}</red-blue-result>");

        Assert.True(result.IsStructured);
        Assert.Equal(expected, result.Winner);
        Assert.DoesNotContain("red-blue-result", result.Decision);
    }

    [Fact]
    public void Parse_MentionsBothSidesWithoutExplicitWinner_DoesNotMisjudgeRed()
    {
        var result = RedBlueJudgeParser.Parse("红方方案更激进，蓝方方案更稳健，需要继续收集证据。");

        Assert.False(result.IsStructured);
        Assert.Equal("平局", result.Winner);
    }

    [Fact]
    public void Parse_ExplicitLegacyWinner_UsesSafeFallback()
    {
        var result = RedBlueJudgeParser.Parse("双方都有合理之处。最终胜方：蓝方。下一轮继续验证。");

        Assert.False(result.IsStructured);
        Assert.Equal("蓝方", result.Winner);
    }

    [Theory]
    [InlineData(new[] { "红方", "蓝方" }, "平局")]
    [InlineData(new[] { "红方", "红方", "蓝方" }, "红方")]
    [InlineData(new[] { "蓝方", "平局", "蓝方" }, "蓝方")]
    public void ResolveOverallWinner_UsesDeterministicVote(string[] winners, string expected)
    {
        Assert.Equal(expected, RedBlueJudgeParser.ResolveOverallWinner(winners));
    }
}

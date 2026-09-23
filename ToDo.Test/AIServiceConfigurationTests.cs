using Microsoft.Extensions.Configuration;
using ToDo.Domain.AI;

namespace ToDo.Test;

public class AIServiceConfigurationTests
{
    [Fact]
    public void Constructor_WhenApiUrlIsMissing_DoesNotFallBackToAnotherProvider()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["AI:ApiKey"] = "test-deepseek-key",
            ["AI:ModelName"] = "deepseek-chat"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => new AIService(configuration));

        Assert.Contains("AI:ApiUrl", exception.Message);
        Assert.DoesNotContain("openai", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_WithExplicitDeepSeekConfiguration_IsAccepted()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["AI:ApiKey"] = "test-deepseek-key",
            ["AI:ApiUrl"] = "https://api.deepseek.com/chat/completions",
            ["AI:ModelName"] = "deepseek-chat"
        });

        var exception = Record.Exception(() => new AIService(configuration));

        Assert.Null(exception);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}

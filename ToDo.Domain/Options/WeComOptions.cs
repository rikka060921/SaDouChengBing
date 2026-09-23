namespace ToDo.Domain.Options;

public sealed class WeComOptions
{
    public bool Enabled { get; set; }
    public string CorpId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://qyapi.weixin.qq.com";
    public int TimeoutSeconds { get; set; } = 15;
}

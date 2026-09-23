namespace ToDo.Domain.Options;

public sealed class IntegritySigningOptions
{
    public string CurrentKeyId { get; set; } = "v1";
    /// <summary>Base64 或至少 32 字符的服务端密钥；生产环境必须通过密钥管理或环境变量提供。</summary>
    public string CurrentKey { get; set; } = string.Empty;
    public Dictionary<string, string> PreviousKeys { get; set; } = new(StringComparer.Ordinal);
}

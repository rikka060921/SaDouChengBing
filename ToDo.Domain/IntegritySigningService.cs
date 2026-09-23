using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ToDo.Domain.Options;

namespace ToDo.Domain;

/// <summary>对已经规范化的 SHA-256 摘要追加服务端 HMAC，支持按 KeyId 平滑轮换。</summary>
public sealed class IntegritySigningService
{
    private readonly IntegritySigningOptions _options;

    public IntegritySigningService(IOptions<IntegritySigningOptions> options) => _options = options.Value;

    public bool IsConfigured => TryDecodeKey(_options.CurrentKey, out _);
    public string CurrentKeyId => IsConfigured ? NormalizeKeyId(_options.CurrentKeyId) : string.Empty;

    public string SignHash(string hash)
    {
        if (!TryDecodeKey(_options.CurrentKey, out var key)) return string.Empty;
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(hash))).ToLowerInvariant();
    }

    public bool VerifyHash(string hash, string? signature, string? keyId)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(keyId)) return false;
        string? configuredKey = string.Equals(NormalizeKeyId(keyId), NormalizeKeyId(_options.CurrentKeyId), StringComparison.Ordinal)
            ? _options.CurrentKey
            : _options.PreviousKeys.GetValueOrDefault(keyId.Trim());
        if (!TryDecodeKey(configuredKey, out var key)) return false;
        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(hash));
        try
        {
            var actual = Convert.FromHexString(signature.Trim());
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException) { return false; }
    }

    public static bool IsProductionKeyValid(string? configuredKey)
        => TryDecodeKey(configuredKey, out _);

    private static bool TryDecodeKey(string? configured, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(configured)) return false;
        try
        {
            var decoded = Convert.FromBase64String(configured.Trim());
            if (decoded.Length >= 32) { key = decoded; return true; }
        }
        catch (FormatException) { }
        var utf8 = Encoding.UTF8.GetBytes(configured);
        if (utf8.Length < 32) return false;
        key = utf8;
        return true;
    }

    private static string NormalizeKeyId(string? keyId)
    {
        var normalized = keyId?.Trim() ?? string.Empty;
        return normalized.Length <= 40 ? normalized : normalized[..40];
    }
}

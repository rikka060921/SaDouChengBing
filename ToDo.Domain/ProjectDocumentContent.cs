using Microsoft.Extensions.Configuration;
using ToDo.Domain.AI;

namespace ToDo.Domain;

public sealed record ProjectDocumentContent(string Content, string Notice, bool CanUseForAI, bool IsTruncated = false);

/// <summary>Explicit capabilities of models served by the configured AI endpoint. Never guess from a vendor name.</summary>
public static class DocumentModelPolicy
{
    public const int MaxImageBytes = 4 * 1024 * 1024;
    public const int MaxImages = 3;
    public static bool SupportsImages(IConfiguration? configuration, string? modelName)
    {
        if (configuration == null) return false;
        var model = string.IsNullOrWhiteSpace(modelName) ? configuration["AI:ModelName"] : modelName.Trim();
        if (string.IsNullOrWhiteSpace(model)) return false;
        return configuration.GetSection("AI:VisionModels").GetChildren()
            .Any(item => string.Equals(item.Value, model, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsImage(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp";
    public static bool IsEditableText(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() is ".md" or ".markdown" or ".txt";

    public static string? GetImageMime(ReadOnlySpan<byte> bytes, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension == ".png" && bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (extension is ".jpg" or ".jpeg" && bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255) return "image/jpeg";
        if (extension == ".webp" && bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    public static async Task<AIImageInput> ReadImageAsync(string path, string fileName, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("图片原件不存在，请重新上传");
        if (new FileInfo(path).Length > MaxImageBytes) throw new InvalidOperationException("AI 图片读取限每张 4 MB，请压缩后重试；原件仍可下载");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (bytes.Length > MaxImageBytes) throw new InvalidOperationException("AI 图片读取限每张 4 MB");
        var mime = GetImageMime(bytes, fileName) ?? throw new InvalidOperationException("图片内容与扩展名不匹配，仅支持 PNG、JPEG、WebP");
        return new AIImageInput { FileName = fileName, MediaType = mime, Data = bytes };
    }
}

using System.IO;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace ToDo.Razor.Utilities;

public static class DocParser
{
    public static async Task<string> ParseAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return "[文件不存在]";

        var ext = Path.GetExtension(filePath).ToLower();

        try
        {
            return ext switch
            {
                ".txt" or ".md" or ".json" or ".csv" or ".xml" or ".yml" or ".yaml" =>
                    await ReadTextWithAutoDetectAsync(filePath),
                ".docx" => await ParseDocxAsync(filePath),
                ".doc" => "[.doc 旧格式不支持在线预览，请下载后查看]",
                ".pdf" => ParsePdf(filePath),
                _ => $"[该文件类型（{ext}）暂不支持在线预览，请下载后查看]"
            };
        }
        catch (Exception ex)
        {
            return $"[解析文件时出错：{ex.Message}]";
        }
    }

    /// <summary>
    /// 自动检测编码读取文本文件
    /// </summary>
    private static async Task<string> ReadTextWithAutoDetectAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        using var reader = new StreamReader(stream, true);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// 解析 docx 文件，兼容多种情况
    /// </summary>
    private static async Task<string> ParseDocxAsync(string filePath)
    {
        try
        {
            // 先读取文件字节，判断是否真的是 docx (zip 格式，以 PK 开头)
            var bytes = await File.ReadAllBytesAsync(filePath);
            
            // docx 是 zip 压缩格式，以 0x50 0x4B (PK) 开头
            if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B)
            {
                // 不是 zip 格式，可能是旧版 .doc 被改名为 .docx，或者是 WPS 特殊格式
                // 尝试提取可读文本
                return ExtractReadableText(bytes);
            }

            // 标准 docx 解析
            using var stream = new MemoryStream(bytes);
            using var doc = WordprocessingDocument.Open(stream, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null) return "[文档内容为空]";

            var sb = new StringBuilder();
            
            // 提取所有段落文本
            foreach (var para in body.Elements<Paragraph>())
            {
                var text = para.InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine(text);
                }
            }

            // 如果段落没有提取到内容，尝试从所有 Text 节点提取
            if (sb.Length == 0)
            {
                var allText = body.InnerText;
                if (!string.IsNullOrWhiteSpace(allText))
                {
                    sb.AppendLine(allText);
                }
            }

            return sb.Length > 0 ? sb.ToString() : "[文档内容为空]";
        }
        catch (Exception ex)
        {
            // 最后兜底：尝试提取可读文本
            try
            {
                var bytes = await File.ReadAllBytesAsync(filePath);
                var text = ExtractReadableText(bytes);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
            catch { }
            
            return $"[解析docx文件出错：{ex.Message}]";
        }
    }

    /// <summary>
    /// 从二进制数据中提取可读文本（兜底方案）
    /// </summary>
    private static string ExtractReadableText(byte[] bytes)
    {
        var sb = new StringBuilder();
        
        // 尝试 UTF-8 解码
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            // 过滤掉不可打印字符，只保留中文、英文、数字、常见标点
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch) || char.IsPunctuation(ch) || char.IsWhiteSpace(ch) || 
                    (int)ch > 0x4E00 && (int)ch < 0x9FFF) // 中文字符范围
                {
                    sb.Append(ch);
                }
            }
        }
        catch { }

        return sb.ToString().Trim();
    }

    private static string ParsePdf(string filePath)
    {
        try
        {
            using var document = PdfDocument.Open(filePath);
            var sb = new StringBuilder();

            foreach (var page in document.GetPages())
            {
                var text = page.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine(text);
                }
            }

            return sb.Length > 0 ? sb.ToString() : "[文档内容为空]";
        }
        catch (Exception ex)
        {
            return $"[解析pdf文件出错：{ex.Message}]";
        }
    }
}

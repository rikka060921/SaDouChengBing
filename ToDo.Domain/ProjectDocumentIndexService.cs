using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ToDo.Context;
using ToDo.Entities;
using UglyToad.PdfPig;
using A = DocumentFormat.OpenXml.Drawing;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ToDo.Domain;

public sealed class ProjectDocumentIndexService
{
    public static readonly TimeSpan StaleIndexTimeout = TimeSpan.FromMinutes(15);
    private const int MaxAttempts = 3;
    private readonly ApplicationDbContext _context;
    private readonly ProjectDocumentService _documents;
    private readonly ILogger<ProjectDocumentIndexService> _logger;

    public ProjectDocumentIndexService(
        ApplicationDbContext context,
        ProjectDocumentService documents,
        ILogger<ProjectDocumentIndexService> logger)
    {
        _context = context;
        _documents = documents;
        _logger = logger;
    }

    public async Task RecoverStaleAsync(CancellationToken cancellationToken = default)
    {
        var staleBefore = AppTime.Now.Subtract(StaleIndexTimeout);
        await _context.ProjectDocuments
            .Where(document => document.IndexStatus == ProjectDocumentIndexStatus.Processing
                && document.IndexLockedAt.HasValue
                && document.IndexLockedAt < staleBefore)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(document => document.IndexStatus, ProjectDocumentIndexStatus.Failed)
                .SetProperty(document => document.IndexLockedAt, (DateTime?)null)
                .SetProperty(document => document.IndexNextRetryAt, AppTime.Now)
                .SetProperty(document => document.IndexError, "索引进程中断，已自动释放锁并等待重试"),
                cancellationToken);
    }

    public async Task<int?> ClaimNextAsync(CancellationToken cancellationToken = default)
    {
        var now = AppTime.Now;
        var id = await _context.ProjectDocuments.AsNoTracking()
            .Where(document => document.IsCurrent
                && document.IndexAttemptCount < MaxAttempts
                && (document.IndexStatus == ProjectDocumentIndexStatus.Pending
                    || (document.IndexStatus == ProjectDocumentIndexStatus.Failed
                        && (!document.IndexNextRetryAt.HasValue || document.IndexNextRetryAt <= now))))
            .OrderBy(document => document.UploadedAt)
            .Select(document => (int?)document.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (!id.HasValue) return null;

        var affected = await _context.ProjectDocuments
            .Where(document => document.Id == id.Value
                && document.IsCurrent
                && document.IndexAttemptCount < MaxAttempts
                && (document.IndexStatus == ProjectDocumentIndexStatus.Pending
                    || (document.IndexStatus == ProjectDocumentIndexStatus.Failed
                        && (!document.IndexNextRetryAt.HasValue || document.IndexNextRetryAt <= now))))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(document => document.IndexStatus, ProjectDocumentIndexStatus.Processing)
                .SetProperty(document => document.IndexAttemptCount, document => document.IndexAttemptCount + 1)
                .SetProperty(document => document.IndexLockedAt, now)
                .SetProperty(document => document.IndexError, string.Empty),
                cancellationToken);
        if (affected != 1) return null;

        var tracked = _context.ChangeTracker.Entries<ProjectDocument>()
            .FirstOrDefault(entry => entry.Entity.Id == id.Value);
        if (tracked != null) await tracked.ReloadAsync(cancellationToken);
        return id;
    }

    public async Task ProcessAsync(int documentId, CancellationToken cancellationToken = default)
    {
        var document = await _context.ProjectDocuments
            .FirstOrDefaultAsync(item => item.Id == documentId, cancellationToken);
        if (document == null || document.IndexStatus != ProjectDocumentIndexStatus.Processing) return;

        try
        {
            var fullPath = _documents.ResolveStoragePath(document.StoragePath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("资料文件不存在", fullPath);
            var text = await ExtractTextAsync(fullPath, document.FileName, cancellationToken);
            text = NormalizeText(text);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("文件中没有可索引的文本内容");

            var chunks = SplitIntoChunks(text);
            await _context.ProjectDocumentChunks
                .Where(chunk => chunk.ProjectDocumentId == document.Id)
                .ExecuteDeleteAsync(cancellationToken);
            _context.ProjectDocumentChunks.AddRange(chunks.Select((chunk, index) => new ProjectDocumentChunk
            {
                ProjectDocumentId = document.Id,
                ProjectId = document.ProjectId,
                ChunkIndex = index,
                Heading = ExtractHeading(chunk),
                Content = chunk,
                CharacterCount = chunk.Length
            }));
            document.IndexStatus = ProjectDocumentIndexStatus.Ready;
            document.IndexedAt = AppTime.Now;
            document.IndexLockedAt = null;
            document.IndexNextRetryAt = null;
            document.IndexError = string.Empty;
            document.ExtractedCharacterCount = text.Length;
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (UnsupportedDocumentException ex)
        {
            document.IndexStatus = ProjectDocumentIndexStatus.Unsupported;
            document.IndexLockedAt = null;
            document.IndexNextRetryAt = null;
            document.IndexError = ex.Message;
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            document.IndexStatus = ProjectDocumentIndexStatus.Failed;
            document.IndexLockedAt = null;
            document.IndexNextRetryAt = AppTime.Now;
            document.IndexError = "索引服务停止，等待恢复";
            await _context.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            var safeError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            document.IndexStatus = ProjectDocumentIndexStatus.Failed;
            document.IndexLockedAt = null;
            document.IndexNextRetryAt = document.IndexAttemptCount >= MaxAttempts
                ? null
                : AppTime.Now.AddMinutes(document.IndexAttemptCount switch { 1 => 1, 2 => 5, _ => 15 });
            document.IndexError = safeError;
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(ex, "Project document {DocumentId} indexing failed on attempt {Attempt}",
                document.Id, document.IndexAttemptCount);
        }
    }

    public async Task ReindexAsync(int documentId, CancellationToken cancellationToken = default)
    {
        var document = await _context.ProjectDocuments.FirstOrDefaultAsync(item => item.Id == documentId, cancellationToken)
            ?? throw new InvalidOperationException("资料不存在");
        document.IndexStatus = ProjectDocumentIndexStatus.Pending;
        document.IndexAttemptCount = 0;
        document.IndexLockedAt = null;
        document.IndexNextRetryAt = AppTime.Now;
        document.IndexError = string.Empty;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<string> ExtractTextAsync(string fullPath, string fileName, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".txt" or ".md" or ".markdown" or ".json" or ".xml" or ".csv" or ".log" or ".yaml" or ".yml"
                => await ReadTextAsync(fullPath, cancellationToken),
            ".html" or ".htm" => StripHtml(await ReadTextAsync(fullPath, cancellationToken)),
            ".pdf" => ExtractPdf(fullPath),
            ".docx" => ExtractWord(fullPath),
            ".pptx" => ExtractPresentation(fullPath),
            ".xlsx" => ExtractSpreadsheet(fullPath),
            _ => throw new UnsupportedDocumentException($"暂不支持解析 {extension} 文件；可转换为 PDF、Word、PPT、Excel、Markdown 或纯文本")
        };
    }

    private static async Task<string> ReadTextAsync(string fullPath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(fullPath);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string ExtractPdf(string fullPath)
    {
        using var pdf = PdfDocument.Open(fullPath);
        var builder = new StringBuilder();
        foreach (var page in pdf.GetPages())
            builder.AppendLine($"## 第 {page.Number} 页").AppendLine(page.Text).AppendLine();
        return builder.ToString();
    }

    private static string ExtractWord(string fullPath)
    {
        using var document = WordprocessingDocument.Open(fullPath, false);
        return string.Join("\n", document.MainDocumentPart?.Document?
            .Descendants<W.Paragraph>()
            .Select(paragraph => paragraph.InnerText)
            .Where(text => !string.IsNullOrWhiteSpace(text)) ?? []);
    }

    private static string ExtractPresentation(string fullPath)
    {
        using var document = PresentationDocument.Open(fullPath, false);
        var builder = new StringBuilder();
        var slides = document.PresentationPart?.SlideParts.ToList() ?? [];
        for (var index = 0; index < slides.Count; index++)
        {
            builder.AppendLine($"## 第 {index + 1} 页");
            foreach (var text in slides[index].Slide?.Descendants<A.Text>() ?? [])
                if (!string.IsNullOrWhiteSpace(text.Text)) builder.AppendLine(text.Text);
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string ExtractSpreadsheet(string fullPath)
    {
        using var document = SpreadsheetDocument.Open(fullPath, false);
        var workbook = document.WorkbookPart;
        if (workbook == null) return string.Empty;
        var shared = workbook.SharedStringTablePart?.SharedStringTable;
        var builder = new StringBuilder();
        foreach (var sheet in workbook.Workbook?.Sheets?.Elements<S.Sheet>() ?? [])
        {
            builder.AppendLine($"## 工作表：{sheet.Name}");
            var worksheetPart = workbook.GetPartById(sheet.Id!) as WorksheetPart;
            if (worksheetPart == null) continue;
            foreach (var row in worksheetPart.Worksheet?.Descendants<S.Row>() ?? [])
            {
                var values = row.Elements<S.Cell>().Select(cell => ReadCell(cell, shared));
                builder.AppendLine(string.Join("\t", values));
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string ReadCell(S.Cell cell, S.SharedStringTable? shared)
    {
        var value = cell.CellValue?.InnerText ?? cell.InnerText;
        if (cell.DataType?.Value == S.CellValues.SharedString
            && int.TryParse(value, out var index)
            && shared != null
            && index >= 0
            && index < shared.ChildElements.Count)
            return shared.ChildElements[index].InnerText;
        return value;
    }

    private static string StripHtml(string html) =>
        WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));

    private static string NormalizeText(string text)
    {
        text = text.Replace("\0", string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, "[\\t ]+", " ");
        text = Regex.Replace(text, "\n{3,}", "\n\n");
        return text.Trim();
    }

    internal static List<string> SplitIntoChunks(string text, int targetSize = 1800, int overlap = 200)
    {
        targetSize = Math.Clamp(targetSize, 500, 5000);
        overlap = Math.Clamp(overlap, 0, Math.Min(500, targetSize / 3));
        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length > targetSize)
            {
                FlushCurrent();
                current.Clear();
                for (var start = 0; start < paragraph.Length; start += targetSize - overlap)
                {
                    var length = Math.Min(targetSize, paragraph.Length - start);
                    chunks.Add(paragraph.Substring(start, length).Trim());
                    if (start + length >= paragraph.Length) break;
                }
                continue;
            }

            if (current.Length > 0 && current.Length + paragraph.Length + 2 > targetSize)
                FlushCurrent();
            if (current.Length > 0) current.AppendLine().AppendLine();
            current.Append(paragraph);
        }
        FlushCurrent();
        return chunks.Where(chunk => !string.IsNullOrWhiteSpace(chunk)).ToList();

        void FlushCurrent()
        {
            if (current.Length == 0) return;
            var chunk = current.ToString().Trim();
            chunks.Add(chunk);
            var tailLength = Math.Min(overlap, chunk.Length);
            var tail = tailLength > 0 ? chunk[^tailLength..] : string.Empty;
            current.Clear();
            if (!string.IsNullOrWhiteSpace(tail)) current.Append(tail);
        }
    }

    private static string ExtractHeading(string chunk)
    {
        var firstLine = chunk.Split('\n', 2)[0].Trim().TrimStart('#', '-', '*', ' ');
        return firstLine.Length <= 500 ? firstLine : firstLine[..500];
    }

    private sealed class UnsupportedDocumentException : Exception
    {
        public UnsupportedDocumentException(string message) : base(message) { }
    }
}

public sealed class ProjectDocumentIndexHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProjectDocumentIndexHostedService> _logger;

    public ProjectDocumentIndexHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<ProjectDocumentIndexHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = false;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var index = scope.ServiceProvider.GetRequiredService<ProjectDocumentIndexService>();
                await index.RecoverStaleAsync(stoppingToken);
                var documentId = await index.ClaimNextAsync(stoppingToken);
                if (documentId.HasValue)
                {
                    handled = true;
                    await index.ProcessAsync(documentId.Value, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Project document indexing worker failed");
            }

            if (!handled) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

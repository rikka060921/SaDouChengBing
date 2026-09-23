using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using System.Text;
using System.Text.RegularExpressions;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public class AgentDocumentAccessService
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".xml", ".csv", ".log", ".yaml", ".yml"
    };

    private readonly ApplicationDbContext _context;
    private readonly IHostEnvironment _environment;

    public AgentDocumentAccessService(ApplicationDbContext context, IHostEnvironment environment)
    {
        _context = context;
        _environment = environment;
    }

    public Task<List<AgentDocumentPermission>> GetPermissionsAsync(int projectId, CancellationToken cancellationToken = default)
    {
        return _context.AgentDocumentPermissions.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .OrderBy(item => item.AgentKey)
            .ThenBy(item => item.Category)
            .ToListAsync(cancellationToken);
    }

    public async Task SetPermissionAsync(
        int projectId,
        string agentKey,
        ProjectDocumentCategory category,
        bool canRead,
        bool canWrite,
        int updatedById,
        CancellationToken cancellationToken = default,
        int? categoryId = null)
    {
        var normalizedKey = agentKey.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKey)) throw new InvalidOperationException("请选择 Agent");
        if (!Enum.IsDefined(category)) throw new InvalidOperationException("资料分类无效");
        if (!await _context.AgentDefinitions.AnyAsync(item => item.AgentKey == normalizedKey, cancellationToken))
            throw new InvalidOperationException("Agent 不存在");

        var normalizedCategory = category;
        if (categoryId.HasValue)
        {
            var customCategoryExists = await _context.DocumentCategories.AsNoTracking()
                .AnyAsync(item => item.Id == categoryId.Value && item.ProjectId == projectId, cancellationToken);
            if (!customCategoryExists) throw new InvalidOperationException("自定义资料分类不存在或不属于当前项目");
            // 自定义分类以 CategoryId 为唯一权限边界，枚举分类只保留统一的兼容值。
            normalizedCategory = ProjectDocumentCategory.Other;
        }

        var permission = await _context.AgentDocumentPermissions.FirstOrDefaultAsync(item => item.ProjectId == projectId
            && item.AgentKey == normalizedKey
            && (categoryId.HasValue
                ? item.CategoryId == categoryId
                : item.CategoryId == null && item.Category == normalizedCategory), cancellationToken);
        if (permission == null)
        {
            permission = new AgentDocumentPermission
            {
                ProjectId = projectId,
                AgentKey = normalizedKey,
                Category = normalizedCategory,
                CategoryId = categoryId
            };
            _context.AgentDocumentPermissions.Add(permission);
        }
        else
        {
            permission.Category = normalizedCategory;
        }
        permission.CanWrite = canWrite;
        permission.CanRead = canRead || canWrite;
        permission.UpdatedById = updatedById;
        permission.UpdatedAt = AppTime.Now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> CanReadAsync(
        int projectId,
        string agentKey,
        ProjectDocumentCategory category,
        CancellationToken cancellationToken = default,
        int? categoryId = null)
    {
        return await _context.AgentDocumentPermissions.AsNoTracking().AnyAsync(item => item.ProjectId == projectId
            && item.AgentKey == agentKey
            && (categoryId.HasValue
                ? item.CategoryId == categoryId
                : item.CategoryId == null && item.Category == category)
            && item.CanRead, cancellationToken);
    }

    public async Task<bool> CanWriteAsync(
        int projectId,
        string agentKey,
        ProjectDocumentCategory category,
        CancellationToken cancellationToken = default,
        int? categoryId = null)
    {
        return await _context.AgentDocumentPermissions.AsNoTracking().AnyAsync(item => item.ProjectId == projectId
            && item.AgentKey == agentKey
            && (categoryId.HasValue
                ? item.CategoryId == categoryId
                : item.CategoryId == null && item.Category == category)
            && item.CanWrite, cancellationToken);
    }

    public async Task<List<AgentReadableDocument>> ReadDocumentsAsync(
        int projectId,
        string agentKey,
        int userId,
        int? aiSessionId = null,
        int maxDocuments = 30,
        int maxCharactersPerDocument = 12000,
        CancellationToken cancellationToken = default,
        string? query = null,
        int totalCharacterBudget = 24000)
    {
        if (!await CanUserAccessProjectAsync(projectId, userId, cancellationToken))
            throw new UnauthorizedAccessException("当前用户无权访问该项目资料");

        var permissions = await _context.AgentDocumentPermissions.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.AgentKey == agentKey && item.CanRead)
            .Select(item => new { item.Category, item.CategoryId })
            .ToListAsync(cancellationToken);

        var allowedCategories = permissions.Where(p => !p.CategoryId.HasValue).Select(p => p.Category).ToHashSet();
        var allowedCategoryIds = permissions.Where(p => p.CategoryId.HasValue).Select(p => p.CategoryId!.Value).ToHashSet();

        var documents = await _context.ProjectDocuments.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.IsCurrent)
            .OrderByDescending(item => item.UploadedAt)
            .Take(Math.Clamp(maxDocuments, 1, 100))
            .ToListAsync(cancellationToken);

        var result = new List<AgentReadableDocument>();
        var remainingBudget = Math.Clamp(totalCharacterBudget, 2000, 50000);
        foreach (var document in documents)
        {
            var allowed = document.CategoryId.HasValue
                ? allowedCategoryIds.Contains(document.CategoryId.Value)
                : allowedCategories.Contains(document.Category);
            _context.AgentDocumentAccessLogs.Add(new AgentDocumentAccessLog
            {
                ProjectId = projectId,
                DocumentId = document.Id,
                AiSessionId = aiSessionId,
                UserId = userId,
                AgentKey = agentKey,
                Category = document.Category,
                CategoryId = document.CategoryId,
                AccessType = AgentDocumentAccessType.Read,
                IsAllowed = allowed,
                Reason = allowed ? "分类读取权限已授权" : "默认拒绝：未配置分类读取权限"
            });
            if (!allowed) continue;

            if (remainingBudget < 500) break;
            var documentBudget = Math.Min(Math.Clamp(maxCharactersPerDocument, 500, 12000), remainingBudget);
            var excerpt = await ReadExcerptAsync(document, documentBudget, cancellationToken, query);
            remainingBudget -= Math.Min(remainingBudget, excerpt.Length);

            result.Add(new AgentReadableDocument
            {
                Id = document.Id,
                FileName = document.FileName,
                Category = document.Category,
                VersionNumber = document.VersionNumber,
                Description = document.Description ?? string.Empty,
                UploadedAt = document.UploadedAt,
                IndexStatus = document.IndexStatus,
                ContentExcerpt = excerpt
            });
        }
        await _context.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task LogWriteAttemptAsync(
        int projectId,
        string agentKey,
        ProjectDocumentCategory category,
        bool allowed,
        string reason,
        int userId,
        int? aiSessionId,
        int? documentId = null,
        CancellationToken cancellationToken = default,
        int? categoryId = null)
    {
        _context.AgentDocumentAccessLogs.Add(new AgentDocumentAccessLog
        {
            ProjectId = projectId,
            DocumentId = documentId,
            AiSessionId = aiSessionId,
            UserId = userId,
            AgentKey = agentKey,
            Category = category,
            CategoryId = categoryId,
            AccessType = AgentDocumentAccessType.Write,
            IsAllowed = allowed,
            Reason = reason.Length > 500 ? reason[..500] : reason
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    public Task<List<AgentDocumentAccessLog>> GetRecentLogsAsync(int projectId, int take = 100, CancellationToken cancellationToken = default)
    {
        return _context.AgentDocumentAccessLogs.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .OrderByDescending(item => item.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);
    }

    private async Task<string> ReadExcerptAsync(
        ProjectDocument document,
        int maxCharacters,
        CancellationToken cancellationToken,
        string? query)
    {
        var indexedChunks = await _context.ProjectDocumentChunks.AsNoTracking()
            .Where(chunk => chunk.ProjectDocumentId == document.Id)
            .OrderBy(chunk => chunk.ChunkIndex)
            .Take(500)
            .Select(chunk => new { chunk.ChunkIndex, chunk.Heading, chunk.Content })
            .ToListAsync(cancellationToken);
        if (indexedChunks.Count > 0)
        {
            var terms = ExtractSearchTerms(query);
            var ranked = indexedChunks
                .Select(chunk => new
                {
                    Chunk = chunk,
                    Score = terms.Count == 0
                        ? -chunk.ChunkIndex
                        : terms.Sum(term =>
                            (chunk.Heading.Contains(term, StringComparison.OrdinalIgnoreCase) ? 4 : 0)
                            + (chunk.Content.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1 : 0))
                })
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Chunk.ChunkIndex)
                .Take(terms.Count == 0 ? 3 : 8)
                .ToList();
            if (ranked.All(item => item.Score <= 0))
                ranked = indexedChunks.Take(3).Select(chunk => new { Chunk = chunk, Score = 0 }).ToList();

            var builder = new StringBuilder();
            foreach (var item in ranked)
            {
                var prefix = $"[片段 {item.Chunk.ChunkIndex + 1}]";
                var content = $"{prefix}\n{item.Chunk.Content.Trim()}\n";
                if (builder.Length + content.Length > maxCharacters)
                {
                    var remaining = maxCharacters - builder.Length;
                    if (remaining > prefix.Length + 100) builder.Append(content[..remaining]);
                    break;
                }
                builder.AppendLine(content);
            }
            return builder.ToString().Trim();
        }

        if (!TextExtensions.Contains(Path.GetExtension(document.FileName)))
            return document.IndexStatus switch
            {
                ProjectDocumentIndexStatus.Pending or ProjectDocumentIndexStatus.Processing => "[该文件正在建立知识索引，稍后即可检索正文。]",
                ProjectDocumentIndexStatus.Failed => $"[知识索引失败：{document.IndexError}]",
                ProjectDocumentIndexStatus.Unsupported => $"[暂不支持正文解析：{document.IndexError}]",
                _ => "[该文件暂无可检索正文。]"
            };
        var fullPath = ResolveStoragePath(document.StoragePath);
        if (!File.Exists(fullPath)) return "[资料文件不存在]";
        var text = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var limit = Math.Clamp(maxCharacters, 1000, 50000);
        return text.Length <= limit ? text : text[..limit] + "\n[内容已截断]";
    }

    private static List<string> ExtractSearchTerms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(query, @"[A-Za-z0-9_\-]{2,}|[\p{IsCJKUnifiedIdeographs}]{2,}"))
        {
            var token = match.Value;
            if (token.Length <= 12) result.Add(token);
            if (Regex.IsMatch(token, @"^[\p{IsCJKUnifiedIdeographs}]+$") && token.Length > 2)
            {
                for (var index = 0; index < token.Length - 1; index++)
                    result.Add(token.Substring(index, 2));
            }
            if (result.Count >= 100) break;
        }
        return result.Take(100).ToList();
    }

    private string ResolveStoragePath(string storagePath)
    {
        var normalized = storagePath.Replace('/', Path.DirectorySeparatorChar);
        var relative = normalized.TrimStart(Path.DirectorySeparatorChar);
        string root;
        string storageRoot;
        if (relative.StartsWith($"App_Data{Path.DirectorySeparatorChar}ProjectDocuments{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            root = _environment.ContentRootPath;
            storageRoot = Path.Combine(root, "App_Data", "ProjectDocuments");
        }
        else if (relative.StartsWith($"uploads{Path.DirectorySeparatorChar}projects{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            root = Path.Combine(_environment.ContentRootPath, "wwwroot");
            storageRoot = Path.Combine(root, "uploads", "projects");
        }
        else
        {
            throw new InvalidOperationException("资料存储路径无效");
        }
        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        var allowedRoot = Path.GetFullPath(storageRoot) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("资料存储路径无效");
        return fullPath;
    }

    private async Task<bool> CanUserAccessProjectAsync(int projectId, int userId, CancellationToken cancellationToken)
    {
        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user?.Role == UserRole.systemAdmin) return true;
        return await _context.Project.AsNoTracking().AnyAsync(project => project.Id == projectId
            && !project.IsDeleted
            && (project.LeaderUserId == userId
                || _context.ProjectUsers.Any(member => member.ProjectId == projectId && member.UserId == userId)), cancellationToken);
    }
}

public sealed class AgentReadableDocument
{
    public int Id { get; init; }
    public string FileName { get; init; } = string.Empty;
    public ProjectDocumentCategory Category { get; init; }
    public int VersionNumber { get; init; }
    public string Description { get; init; } = string.Empty;
    public DateTime UploadedAt { get; init; }
    public ProjectDocumentIndexStatus IndexStatus { get; init; }
    public string ContentExcerpt { get; init; } = string.Empty;
}

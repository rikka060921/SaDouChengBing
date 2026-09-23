using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public class ProjectDocumentService
{
    private readonly ApplicationDbContext _context;
    private readonly IHostEnvironment _environment;

    public ProjectDocumentService(ApplicationDbContext context, IHostEnvironment environment)
    {
        _context = context;
        _environment = environment;
    }

    public Task<List<ProjectDocument>> GetDocumentsAsync(int projectId, CancellationToken cancellationToken = default)
    {
        return _context.ProjectDocuments.AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.IsCurrent)
            .OrderByDescending(d => d.FileName)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<ProjectDocument>> GetVersionHistoryAsync(int projectId, int currentDocumentId, CancellationToken cancellationToken = default)
    {
        var currentDoc = await _context.ProjectDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == currentDocumentId && d.ProjectId == projectId, cancellationToken);
        
        if (currentDoc == null) return new List<ProjectDocument>();

        return await _context.ProjectDocuments.AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.FileName == currentDoc.FileName && d.Id != currentDocumentId)
            .OrderByDescending(d => d.VersionNumber)
            .ToListAsync(cancellationToken);
    }

    public async Task<ProjectDocument?> RollbackVersionAsync(int projectId, int versionId, int userId, CancellationToken cancellationToken = default)
    {
        var version = await _context.ProjectDocuments
            .FirstOrDefaultAsync(d => d.Id == versionId && d.ProjectId == projectId, cancellationToken);
        
        if (version == null) return null;

        var currentDoc = await _context.ProjectDocuments
            .FirstOrDefaultAsync(d => d.ProjectId == projectId && d.FileName == version.FileName && d.IsCurrent, cancellationToken);
        
        if (currentDoc != null)
        {
            currentDoc.IsCurrent = false;
        }

        version.IsCurrent = true;
        var hasIndex = await _context.ProjectDocumentChunks.AsNoTracking()
            .AnyAsync(chunk => chunk.ProjectDocumentId == version.Id, cancellationToken);
        if (!hasIndex)
        {
            version.IndexStatus = ProjectDocumentIndexStatus.Pending;
            version.IndexAttemptCount = 0;
            version.IndexLockedAt = null;
            version.IndexNextRetryAt = AppTime.Now;
            version.IndexError = string.Empty;
        }
        _context.ProjectDocuments.Update(version);
        await _context.SaveChangesAsync(cancellationToken);

        return version;
    }

    public async Task<ProjectDocument> SaveAsync(
        int projectId,
        int userId,
        IFormFile file,
        string? description,
        CancellationToken cancellationToken = default,
        ProjectDocumentCategory category = ProjectDocumentCategory.Other)
    {
        if (file == null || file.Length <= 0) throw new InvalidOperationException("资料文件不能为空");
        if (file.Length > 50 * 1024 * 1024) throw new InvalidOperationException("单个资料文件不能超过 50 MB");

        var safeName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(safeName)) throw new InvalidOperationException("文件名无效");

        await ProjectLifecycleRules.RequireActiveAsync(_context, projectId, cancellationToken);
        var uploadRoot = GetSecureUploadRoot(projectId);
        Directory.CreateDirectory(uploadRoot);
        var storageName = $"{Guid.NewGuid():N}_{safeName}";
        var fullPath = Path.Combine(uploadRoot, storageName);

        await using (var stream = File.Create(fullPath)) await file.CopyToAsync(stream, cancellationToken);
        try
        {
            await using var hashStream = File.OpenRead(fullPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
            var latest = await _context.ProjectDocuments.Where(d => d.ProjectId == projectId && d.FileName == safeName).OrderByDescending(d => d.VersionNumber).FirstOrDefaultAsync(cancellationToken);
            var version = (latest?.VersionNumber ?? 0) + 1;
            var conflict = latest != null && !string.Equals(latest.FileHash, hash, StringComparison.OrdinalIgnoreCase);
            if (latest != null) latest.IsCurrent = false;

            var document = new ProjectDocument
            {
                ProjectId = projectId,
                UploadedById = userId,
                Category = category,
                FileName = safeName,
                StoragePath = BuildSecureStoragePath(projectId, storageName),
                FileHash = hash,
                FileSize = file.Length,
                VersionNumber = version,
                IsCurrent = true,
                ConflictDetected = conflict,
                ConflictWithDocumentId = conflict ? latest?.Id : null,
                Description = description?.Trim() ?? string.Empty
            };
            _context.ProjectDocuments.Add(document);
            await _context.SaveChangesAsync(cancellationToken);
            return document;
        }
        catch
        {
            File.Delete(fullPath);
            throw;
        }
    }

    public async Task<ProjectDocument> SaveWithCategoryAsync(
        int projectId,
        int userId,
        IFormFile file,
        string? description,
        CancellationToken cancellationToken = default,
        ProjectDocumentCategory category = ProjectDocumentCategory.Other,
        int? customCategoryId = null,
        string? customCategoryName = null)
    {
        if (file == null || file.Length <= 0) throw new InvalidOperationException("资料文件不能为空");
        if (file.Length > 50 * 1024 * 1024) throw new InvalidOperationException("单个资料文件不能超过 50 MB");

        var safeName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(safeName)) throw new InvalidOperationException("文件名无效");

        await ProjectLifecycleRules.RequireActiveAsync(_context, projectId, cancellationToken);
        var uploadRoot = GetSecureUploadRoot(projectId);
        Directory.CreateDirectory(uploadRoot);
        var storageName = $"{Guid.NewGuid():N}_{safeName}";
        var fullPath = Path.Combine(uploadRoot, storageName);

        await using (var stream = File.Create(fullPath)) await file.CopyToAsync(stream, cancellationToken);
        try
        {
            await using var hashStream = File.OpenRead(fullPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
            var latest = await _context.ProjectDocuments.Where(d => d.ProjectId == projectId && d.FileName == safeName).OrderByDescending(d => d.VersionNumber).FirstOrDefaultAsync(cancellationToken);
            var version = (latest?.VersionNumber ?? 0) + 1;
            var conflict = latest != null && !string.Equals(latest.FileHash, hash, StringComparison.OrdinalIgnoreCase);
            if (latest != null) latest.IsCurrent = false;

            var document = new ProjectDocument
            {
                ProjectId = projectId,
                UploadedById = userId,
                Category = category,
                CategoryId = customCategoryId,
                CategoryName = customCategoryName,
                FileName = safeName,
                StoragePath = BuildSecureStoragePath(projectId, storageName),
                FileHash = hash,
                FileSize = file.Length,
                VersionNumber = version,
                IsCurrent = true,
                ConflictDetected = conflict,
                ConflictWithDocumentId = conflict ? latest?.Id : null,
                Description = description?.Trim()
            };
            _context.ProjectDocuments.Add(document);
            await _context.SaveChangesAsync(cancellationToken);
            return document;
        }
        catch
        {
            File.Delete(fullPath);
            throw;
        }
    }

    public async Task<(string FullPath, string FileName)?> ResolveDownloadAsync(int documentId, int projectId, CancellationToken cancellationToken = default)
    {
        var document = await _context.ProjectDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId && d.ProjectId == projectId, cancellationToken);
        if (document == null) return null;
        var fullPath = ResolveStoragePath(document.StoragePath);
        return File.Exists(fullPath) ? (fullPath, document.FileName) : null;
    }

    public async Task<ProjectDocument> SaveGeneratedTextAsync(
        int projectId,
        int userId,
        string fileName,
        string content,
        string? description,
        CancellationToken cancellationToken = default,
        ProjectDocumentCategory category = ProjectDocumentCategory.Other)
    {
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName)) throw new InvalidOperationException("生成资料的文件名无效");
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("生成资料的内容不能为空");
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidOperationException("生成资料不能超过 2 MB");

        await ProjectLifecycleRules.RequireActiveAsync(_context, projectId, cancellationToken);
        var uploadRoot = GetSecureUploadRoot(projectId);
        Directory.CreateDirectory(uploadRoot);
        var storageName = $"{Guid.NewGuid():N}_{safeName}";
        var fullPath = Path.Combine(uploadRoot, storageName);
        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);

        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var latest = await _context.ProjectDocuments
                .Where(item => item.ProjectId == projectId && item.FileName == safeName)
                .OrderByDescending(item => item.VersionNumber)
                .FirstOrDefaultAsync(cancellationToken);
            var version = (latest?.VersionNumber ?? 0) + 1;
            var conflict = latest != null && !string.Equals(latest.FileHash, hash, StringComparison.OrdinalIgnoreCase);
            if (latest != null) latest.IsCurrent = false;

            var document = new ProjectDocument
            {
                ProjectId = projectId,
                UploadedById = userId,
                Category = category,
                FileName = safeName,
                StoragePath = BuildSecureStoragePath(projectId, storageName),
                FileHash = hash,
                FileSize = bytes.Length,
                VersionNumber = version,
                IsCurrent = true,
                ConflictDetected = conflict,
                ConflictWithDocumentId = conflict ? latest?.Id : null,
                Description = description?.Trim() ?? string.Empty
            };
            _context.ProjectDocuments.Add(document);
            await _context.SaveChangesAsync(cancellationToken);
            return document;
        }
        catch
        {
            File.Delete(fullPath);
            throw;
        }
    }

    public async Task<string> SaveDraftTextAsync(
        int projectId,
        string fileName,
        string content,
        CancellationToken cancellationToken = default)
    {
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName)) throw new InvalidOperationException("草稿文件名无效");
        var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidOperationException("草稿不能超过 2 MB");
        await ProjectLifecycleRules.RequireActiveAsync(_context, projectId, cancellationToken);
        var root = Path.Combine(GetSecureUploadRoot(projectId), "drafts");
        Directory.CreateDirectory(root);
        var storageName = $"{Guid.NewGuid():N}_{safeName}";
        await File.WriteAllBytesAsync(Path.Combine(root, storageName), bytes, cancellationToken);
        return Path.Combine("App_Data", "ProjectDocuments", projectId.ToString(), "drafts", storageName)
            .Replace('\\', '/');
    }

    public string ResolveStoragePath(string storagePath)
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

    private string GetSecureUploadRoot(int projectId) =>
        Path.Combine(_environment.ContentRootPath, "App_Data", "ProjectDocuments", projectId.ToString());

    private static string BuildSecureStoragePath(int projectId, string storageName) =>
        Path.Combine("App_Data", "ProjectDocuments", projectId.ToString(), storageName).Replace('\\', '/');
}

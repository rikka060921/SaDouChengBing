using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public sealed class ProjectHistoryBackfillFile
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public string Hash { get; set; } = "";
}

public sealed class ProjectHistoryBackfillManifest
{
    public int FormatVersion { get; set; } = 1;
    public string ProjectName { get; set; } = "撒豆成兵（正式开发）";
    public string LeaderUserName { get; set; } = "hongenze";
    public List<ProjectHistoryImportItem> Timeline { get; set; } = [];
    public List<ProjectHistoryBackfillFile> Files { get; set; } = [];
}

public sealed record ProjectHistoryBackfillResult(int ProjectId, bool CreatedProject, int TimelineAdded, int MembersAdded,
    int DocumentsAdded, int FilesRestored, int Skipped, IReadOnlyList<string> UnmatchedPeople, IReadOnlyList<string> Warnings);

/// <summary>Offline, transactional and idempotent backfill. Never creates live tasks, meetings, passwords or historical audit entries.</summary>
public sealed class ProjectHistoryBackfillService(ApplicationDbContext context, ProjectDocumentService documents)
{
    public async Task<ProjectHistoryBackfillResult> ApplyAsync(string bundleRoot, ProjectHistoryBackfillManifest manifest, int actorId, CancellationToken ct = default)
    {
        if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(manifest.ProjectName) || manifest.ProjectName.Length > 255
            || manifest.Files is null || manifest.Files.Count is < 1 or > 200 || manifest.Files.Any(f => f is null)
            || manifest.Files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count)
            throw new ArgumentException("补录清单格式、文件数量或文件名无效");
        var timeline = ProjectHistoryImport.Parse(JsonSerializer.Serialize(manifest.Timeline));
        var verified = new List<(ProjectHistoryBackfillFile File, byte[] Bytes)>();
        long totalBytes = 0;
        foreach (var item in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 255 || System.IO.Path.GetFileName(item.Name) != item.Name
                || string.IsNullOrWhiteSpace(item.Category) || item.Category.Length > 50 || item.Description is null || item.Description.Length > 4000)
                throw new ArgumentException("补录文件名或分类无效");
            var path = ResolveBundlePath(bundleRoot, item.Path);
            var size = new FileInfo(path).Length;
            totalBytes += size;
            if (size is <= 0 or > 50 * 1024 * 1024 || totalBytes > 200 * 1024 * 1024) throw new ArgumentException("补录文件大小无效或总计超过 200 MB");
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (bytes.Length != size) throw new ArgumentException("读取期间补录文件发生变化，请重新打包");
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), item.Hash, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"补录文件校验失败：{item.Name}");
            verified.Add((item, bytes));
        }
        var ledgerInput = verified.SingleOrDefault(v => v.File.Name == ProjectDevelopmentLedger.FileName);
        if (ledgerInput.File is null) throw new ArgumentException("缺少开发记录清单");
        var ledger = ProjectDevelopmentLedger.Parse(Encoding.UTF8.GetString(ledgerInput.Bytes), 0);
        var actor = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == actorId && !u.IsDeleted && u.Status == UserStatus.Active, ct)
            ?? throw new UnauthorizedAccessException("补录操作账号不可用");
        var leader = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserName == manifest.LeaderUserName && !u.IsDeleted && u.Status == UserStatus.Active, ct)
            ?? throw new InvalidOperationException("未找到有效项目负责人账号，不创建或重置账号");
        var candidates = await context.Project.Where(p => p.Name == manifest.ProjectName && !p.IsDeleted).ToListAsync(ct);
        if (candidates.Count > 1) throw new InvalidOperationException("存在多个同名正式项目，请先人工确认，不任意选择");
        var project = candidates.SingleOrDefault(); var created = project is null;
        if (created && actor.Role != UserRole.systemAdmin && actor.Id != leader.Id) throw new UnauthorizedAccessException("仅负责人本人或系统管理员可建立该正式项目");
        if (!created && actor.Role != UserRole.systemAdmin && project!.LeaderUserId != actor.Id
            && !await context.ProjectUsers.AnyAsync(m => m.ProjectId == project.Id && m.UserId == actor.Id && m.ProjectRole == (int)ProjectRole.Admin, ct))
            throw new UnauthorizedAccessException("无权补录该项目");
        if (!created) await ProjectLifecycleRules.RequireActiveAsync(context, project!.Id, ct);
        var addedPaths = new List<string>(); var warnings = new List<string>(); var unmatched = new List<string>();
        var nodesAdded = 0; var membersAdded = 0; var documentsAdded = 0; var restored = 0; var skipped = 0;
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        try
        {
            if (project is null)
            {
                project = new Project { Name = manifest.ProjectName, LeaderUserId = leader.Id, CreatedByUserId = actor.Id, CreatedAt = AppTime.Now,
                    Description = "面向中小团队的 AI 辅助项目管理系统。历史记录按来源补录，不当作当前任务、验收或原始系统操作。" };
                context.Project.Add(project); await context.SaveChangesAsync(ct);
            }
            var actorNames = ledger.Participants.Select(p => p.UserName).Append(manifest.LeaderUserName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var userName in actorNames)
            {
                var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserName == userName && !u.IsDeleted && u.Status == UserStatus.Active, ct);
                if (user is null) { unmatched.Add(userName); continue; }
                if (!await context.ProjectUsers.AnyAsync(m => m.ProjectId == project.Id && m.UserId == user.Id, ct))
                { context.ProjectUsers.Add(new ProjectUser { ProjectId = project.Id, UserId = user.Id, ProjectRole = user.Id == project.LeaderUserId ? (int)ProjectRole.Admin : (int)ProjectRole.Member }); membersAdded++; }
            }
            var existingKeys = (await context.ProjectHistoryEntries.Where(h => h.ProjectId == project.Id).Select(h => h.SourceKey).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in timeline.Where(i => !existingKeys.Contains(i.Key)))
            { context.ProjectHistoryEntries.Add(new ProjectHistoryEntry { ProjectId = project.Id, SourceKey = item.Key, Period = item.Period, SortDate = item.SortDate, Title = item.Title, Summary = item.Summary, Sources = item.Sources, ImportedByUserId = actor.Id, ImportedAt = AppTime.Now }); nodesAdded++; }
            await context.SaveChangesAsync(ct);
            foreach (var (item, originalBytes) in verified)
            {
                var bytes = originalBytes;
                if (item.Name == ProjectDevelopmentLedger.FileName)
                {
                    ledger.ProjectId = project.Id;
                    var oldDocument = await context.ProjectDocuments.AsNoTracking().Where(d => d.ProjectId == project.Id && d.IsCurrent && d.FileName == item.Name)
                        .OrderByDescending(d => d.VersionNumber).FirstOrDefaultAsync(ct);
                    var oldFile = oldDocument == null ? null : await documents.ResolveDownloadAsync(oldDocument.Id, project.Id, ct);
                    var previous = oldFile == null ? null : await new ProjectDevelopmentLedgerReader(context, documents).ReadAsync(project.Id, actor.Id, ct);
                    if (oldDocument != null && oldFile == null) warnings.Add("开发记录原件缺失，使用经过校验的来源修复；旧数据库记录保留");
                    if (previous != null)
                    {
                        var oldLedger = previous.Value.Ledger;
                        var known = oldLedger.Changes.Select(c => c.Commit).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var incoming = ledger.Changes.Where(c => !known.Contains(c.Commit)).ToList();
                        oldLedger.Changes.AddRange(incoming);
                        foreach (var person in ledger.Participants.Where(p => !oldLedger.Participants.Any(old => old.UserName.Equals(p.UserName, StringComparison.OrdinalIgnoreCase)))) oldLedger.Participants.Add(person);
                        if (incoming.Count > 0) { oldLedger.SourceHead = ledger.SourceHead; oldLedger.CapturedAt = ledger.CapturedAt; }
                        ledger = oldLedger;
                    }
                    bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ledger, ProjectDevelopmentLedger.JsonOptions));
                    _ = ProjectDevelopmentLedger.Parse(Encoding.UTF8.GetString(bytes), project.Id);
                }
                if (item.Name == ProjectArchivePackage.FileName)
                {
                    var archive = JsonSerializer.Deserialize<ProjectArchivePackage>(Encoding.UTF8.GetString(bytes), ProjectDevelopmentLedger.JsonOptions) ?? throw new ArgumentException("历史档案为空");
                    _ = ProjectArchivePackage.Parse(Encoding.UTF8.GetString(bytes), archive.ProjectId);
                    archive.ProjectId = project.Id;
                    // Source database IDs are NOT portable. Keep the origin label, never link to an unrelated local task.
                    foreach (var record in archive.Records) { record.SourceUrl = ""; record.SourceLabel = "原站历史快照（非本地编号）；" + record.SourceLabel; }
                    var old = await context.ProjectDocuments.AsNoTracking().Where(d => d.ProjectId == project.Id && d.FileName == item.Name && d.IsCurrent).OrderByDescending(d => d.VersionNumber).FirstOrDefaultAsync(ct);
                    if (old != null)
                    {
                        var download = await documents.ResolveDownloadAsync(old.Id, project.Id, ct);
                        if (download != null)
                        {
                            var preserved = ProjectArchivePackage.Parse(await File.ReadAllTextAsync(download.Value.FullPath, ct), project.Id);
                            var oldKeys = preserved.Records.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                            foreach (var record in archive.Records.Where(r => !oldKeys.Contains(r.Key))) preserved.Records.Add(record);
                            archive = preserved; // Existing, possibly newer source snapshots win; never overwrite user records.
                        }
                    }
                    bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(archive, ProjectDevelopmentLedger.JsonOptions));
                    _ = ProjectArchivePackage.Parse(Encoding.UTF8.GetString(bytes), project.Id);
                }
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                var current = await context.ProjectDocuments.Where(d => d.ProjectId == project.Id && d.FileName == item.Name && d.IsCurrent)
                    .OrderByDescending(d => d.VersionNumber).FirstOrDefaultAsync(ct);
                if (current != null && string.Equals(current.FileHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    var destination = documents.ResolveStoragePath(current.StoragePath);
                    if (!File.Exists(destination))
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        await output.WriteAsync(bytes, ct); restored++; continue;
                    }
                    await using var input = File.OpenRead(destination);
                    if (Convert.ToHexString(await SHA256.HashDataAsync(input, ct)) == hash) { skipped++; continue; }
                    warnings.Add($"{item.Name} 原文件与记录哈希不符；保留原文件，另存版本");
                }
                var category = current?.CategoryId != null ? await context.DocumentCategories.FindAsync([current.CategoryId.Value], ct)
                    : await context.DocumentCategories.SingleOrDefaultAsync(c => c.ProjectId == project.Id && c.Name == item.Category, ct);
                if (category == null) { category = new DocumentCategory { ProjectId = project.Id, Name = item.Category, Description = "历史补录资料；保留来源与版本" }; context.DocumentCategories.Add(category); await context.SaveChangesAsync(ct); }
                await using var content = new MemoryStream(bytes);
                var saved = await documents.SaveWithCategoryAsync(project.Id, actor.Id, new FormFile(content, 0, bytes.Length, "file", item.Name), item.Description, ct,
                    current?.Category ?? ProjectDocumentCategory.Other, category.Id, category.Name);
                addedPaths.Add(documents.ResolveStoragePath(saved.StoragePath)); documentsAdded++;
            }
            if (created || nodesAdded + membersAdded + documentsAdded + restored > 0)
                context.ChangeLogs.Add(new ChangeLog { ProjectId = project.Id, ProjectName = project.Name, TargetId = project.Id, TargetName = project.Name,
                    OperationType = OperationType.更新, OperationTarget = OperationTarget.项目, OperatedAt = AppTime.Now,
                    OperatedByUserId = actor.Id, OperatedByUserName = actor.UserName, OperatedByRealName = actor.RealName,
                    AfterContent = $"[历史补录] Git 来源 {ledger.SourceHead}；新增里程碑 {nodesAdded}，项目成员 {membersAdded}，资料版本 {documentsAdded}，恢复缺失原件 {restored}；历史提交、任务、会议不伪造为本次执行或旧审计。" });
            await context.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return new(project.Id, created, nodesAdded, membersAdded, documentsAdded, restored, skipped, unmatched, warnings);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            foreach (var path in addedPaths) if (File.Exists(path)) File.Delete(path);
            context.ChangeTracker.Clear(); throw;
        }
    }

    public static string ResolveBundlePath(string root, string relative)
    {
        var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p is ".." or "."))
            throw new ArgumentException("补录文件路径无效");
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new ArgumentException("补录文件不在包内或已缺失");
        for (var current = new FileInfo(full) as FileSystemInfo; current != null && current.FullName.Length >= fullRoot.Length; current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("补录包不允许链接或重解析点");
        return full;
    }
}

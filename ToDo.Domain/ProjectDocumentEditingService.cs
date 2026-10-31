using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public sealed record DocumentEditSnapshot(string FileName, string Content, string Token, int SourceVersion, bool IsDraft);

/// <summary>Human text edits use the same project membership as uploads; AI draft edits require existing project-management rights.</summary>
public sealed class ProjectDocumentEditingService(ApplicationDbContext context, ProjectDocumentService documents)
{
    public const int MaxCharacters = 100000;
    public static string Token(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public async Task<DocumentEditSnapshot> ReadAsync(int projectId, int documentId, int draftId, int userId, CancellationToken ct = default)
    {
        var (_, manager) = await AccessAsync(projectId, userId, ct);
        if ((documentId > 0) == (draftId > 0)) throw new InvalidOperationException("请选择一份资料或一份待审核草稿");
        if (draftId > 0)
        {
            if (!manager) throw new UnauthorizedAccessException("只有项目管理人员可校订待审核 AI 草稿");
            var draft = await context.AiDocumentDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.Id == draftId && d.ProjectId == projectId, ct)
                ?? throw new InvalidOperationException("草稿不存在");
            if (draft.AuditStatus != DraftAuditStatus.Pending || draft.ExpireTime <= AppTime.Now)
                throw new InvalidOperationException("草稿已处理或过期，不能继续校订");
            if (!await context.ProjectDocuments.AnyAsync(d => d.Id == draft.SourceDocumentId && d.ProjectId == projectId && d.IsCurrent, ct))
                throw new InvalidOperationException("原资料已有新版本，请重新生成草稿，未覆盖任何资料");
            var text = draft.RevisedContent ?? string.Empty;
            return new(draft.DraftFileName, text, Token(text), await context.ProjectDocuments.Where(d => d.Id == draft.SourceDocumentId).Select(d => d.VersionNumber).SingleAsync(ct), true);
        }
        var document = await context.ProjectDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == documentId && d.ProjectId == projectId, ct)
            ?? throw new InvalidOperationException("资料不存在或不属于当前项目");
        if (!document.IsCurrent) throw new InvalidOperationException("该资料不是当前版本，请打开最新版本后编辑");
        if (!DocumentModelPolicy.IsEditableText(document.FileName)) throw new InvalidOperationException("网页直接编辑仅支持 Markdown、TXT；Office/PDF 原件请下载后编辑，或使用 AI 修订稿");
        var path = documents.ResolveStoragePath(document.StoragePath);
        if (!File.Exists(path)) throw new InvalidOperationException("资料原件不存在");
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidOperationException("网页编辑限 2 MB 文本，请下载后处理");
        string content;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (bytes.Length > 2 * 1024 * 1024) throw new InvalidOperationException("网页编辑限 2 MB 文本，请下载后处理");
            var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            content = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException) { throw new InvalidOperationException("文本不是有效 UTF-8，请下载后转换编码再上传，避免保存乱码"); }
        if (content.Length > MaxCharacters) throw new InvalidOperationException("正文超过 10 万字符，请拆分或下载后处理");
        return new(document.FileName, content, Token(content), document.VersionNumber, false);
    }

    public async Task SaveAsync(int projectId, int documentId, int draftId, int userId, string expectedToken, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > MaxCharacters || Encoding.UTF8.GetByteCount(content) > 2 * 1024 * 1024)
            throw new InvalidOperationException("请输入有效正文，最多 10 万字符、2 MB");
        var snapshot = await ReadAsync(projectId, documentId, draftId, userId, ct);
        if (!string.Equals(expectedToken, snapshot.Token, StringComparison.Ordinal))
            throw new InvalidOperationException("内容已被其他操作更新。你的输入仍保留，请先复制，再刷新对比最新版本");
        if (content == snapshot.Content) return;
        var (actor, _) = await AccessAsync(projectId, userId, ct);
        string? createdPath = null;
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        try
        {
            int targetId;
            string? beforeDraftPath = null;
            if (draftId > 0)
            {
                var draft = await context.AiDocumentDrafts.AsNoTracking().SingleAsync(d => d.Id == draftId && d.ProjectId == projectId, ct);
                beforeDraftPath = draft.DraftFilePath;
                if (!string.Equals(Token(draft.RevisedContent ?? string.Empty), expectedToken, StringComparison.Ordinal))
                    throw new InvalidOperationException("草稿刚被校订，请复制输入后刷新对比；未覆盖最新结果");
                var stem = Path.GetFileNameWithoutExtension(snapshot.FileName);
                var name = DocumentModelPolicy.IsEditableText(snapshot.FileName) ? snapshot.FileName : stem[..Math.Min(160, stem.Length)] + "_校订.md";
                var path = await documents.SaveDraftTextAsync(projectId, name, content, ct);
                createdPath = documents.ResolveStoragePath(path);
                var updated = await context.AiDocumentDrafts.Where(d => d.Id == draftId && d.ProjectId == projectId && d.AuditStatus == DraftAuditStatus.Pending
                    && d.ExpireTime > AppTime.Now && d.DraftFilePath == draft.DraftFilePath && d.RevisedContent == draft.RevisedContent)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.RevisedContent, content).SetProperty(d => d.DraftFilePath, path).SetProperty(d => d.DraftFileName, name), ct);
                if (updated != 1) throw new InvalidOperationException("草稿刚被校订或审核，请复制输入后刷新；未覆盖最新结果");
                var trackedDraft = context.ChangeTracker.Entries<AiDocumentDraft>().FirstOrDefault(e => e.Entity.Id == draftId);
                if (trackedDraft != null) await trackedDraft.ReloadAsync(ct);
                targetId = draftId;
            }
            else
            {
                var revision = await documents.SaveRevisionAsync(projectId, documentId, userId, content, "成员在网页中校订正文", ct);
                createdPath = documents.ResolveStoragePath(revision.StoragePath);
                targetId = revision.Id;
            }
            context.ChangeLogs.Add(new ChangeLog
            {
                ProjectId = projectId, OperationType = OperationType.更新, OperationTarget = OperationTarget.项目,
                OperationStatus = OperationStatus.成功, OperatedByUserId = userId, OperatedByUserName = actor.UserName,
                OperatedByRealName = actor.RealName, OperatedAt = AppTime.Now, TargetId = projectId,
                TargetName = snapshot.FileName, BeforeContent = snapshot.IsDraft ? $"草稿 #{draftId}；前一文件：{beforeDraftPath}；正文校验：{snapshot.Token}" : $"资料 #{documentId} v{snapshot.SourceVersion}",
                AfterContent = snapshot.IsDraft ? $"人工校订草稿 #{targetId}，尚未发布；历史草稿文件保留" : $"网页校订正文，新资料 #{targetId}，基于资料 #{documentId} v{snapshot.SourceVersion}；保留历史版本"
            });
            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            if (createdPath != null && File.Exists(createdPath)) File.Delete(createdPath);
            throw;
        }
    }

    private async Task<(ApplicationUser Actor, bool Manager)> AccessAsync(int projectId, int userId, CancellationToken ct)
    {
        var actor = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && !u.IsDeleted && u.Status == UserStatus.Active, ct)
            ?? throw new UnauthorizedAccessException("账号不可用");
        var project = await context.Project.AsNoTracking().SingleOrDefaultAsync(p => p.Id == projectId && !p.IsDeleted, ct)
            ?? throw new UnauthorizedAccessException("项目不存在或不可访问");
        var member = await context.ProjectUsers.AsNoTracking().SingleOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
        var manager = actor.Role == UserRole.systemAdmin || project.LeaderUserId == userId || member?.ProjectRole == (int)ProjectRole.Admin;
        if (!manager && member == null) throw new UnauthorizedAccessException("你不是该项目成员");
        await ProjectLifecycleRules.RequireActiveAsync(context, projectId, ct);
        return (actor, manager);
    }
}

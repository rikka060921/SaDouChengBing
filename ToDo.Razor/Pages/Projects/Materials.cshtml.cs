using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;
using ToDo.Razor.Utilities;

namespace ToDo.Razor.Pages.Projects;

[Authorize]
public class MaterialsModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ProjectDocumentService _documents;
    private readonly ProjectDocumentIndexService _documentIndex;
    private readonly DocumentCategoryService _categories;
    private readonly AgentDocumentAccessService _agentDocumentAccess;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IAIService _aiService;
    private readonly IEventBus _eventBus;
    private readonly ILogger<MaterialsModel> _logger;

    public MaterialsModel(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        ProjectDocumentService documents,
        ProjectDocumentIndexService documentIndex,
        DocumentCategoryService categories,
        AgentDocumentAccessService agentDocumentAccess,
        IAgentRegistry agentRegistry,
        IAIService aiService,
        IEventBus eventBus,
        ILogger<MaterialsModel> logger)
    {
        _context = context;
        _userManager = userManager;
        _documents = documents;
        _documentIndex = documentIndex;
        _categories = categories;
        _agentDocumentAccess = agentDocumentAccess;
        _agentRegistry = agentRegistry;
        _aiService = aiService;
        _eventBus = eventBus;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)] public int ProjectId { get; set; }
    [BindProperty] public IFormFile? UploadFile { get; set; }
    [BindProperty] public string Description { get; set; } = string.Empty;
    [BindProperty] public ProjectDocumentCategory UploadCategory { get; set; } = ProjectDocumentCategory.Other;
    [BindProperty] public int? UploadCategoryId { get; set; }
    [BindProperty] public string? UploadCustomCategory { get; set; }
    [BindProperty(SupportsGet = true)] public string? CategoryFilter { get; set; }
    [BindProperty(SupportsGet = true)] public int? CategoryFilterId { get; set; }
    public ProjectDocumentCategory? PresetCategoryFilter { get; set; }
    [BindProperty] public string PermissionAgentKey { get; set; } = string.Empty;
    [BindProperty] public ProjectDocumentCategory PermissionCategory { get; set; } = ProjectDocumentCategory.Other;
    [BindProperty] public int? PermissionCustomCategoryId { get; set; }
    [BindProperty] public bool PermissionCanRead { get; set; }
    [BindProperty] public bool PermissionCanWrite { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public List<ProjectDocument> Documents { get; set; } = new();
    public List<DocumentCategory> ProjectCategories { get; set; } = new();
    public IReadOnlyList<AgentDefinition> Agents { get; set; } = Array.Empty<AgentDefinition>();
    public List<AgentDocumentPermission> AgentPermissions { get; set; } = new();
    public bool CanManageAgentPermissions { get; set; }
    public bool IsReadOnly { get; set; }
    public List<(AgentDocumentPermission Permission, string AgentDisplayName, string CategoryDisplayName)> PermissionViewList { get; set; } = new();
    public string PresetCategoryNames { get; set; } = string.Empty;
    public Dictionary<int, bool> DocumentWritePermissions { get; set; } = new();
    public List<AiDocumentDraft> PendingDrafts { get; set; } = new();
    public List<AiDocumentDraft> ProcessedDrafts { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await ProjectExistsAsync()) return NotFound();
        if (!await CanAccessAsync()) return Forbid();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync()
    {
        if (!await CanAccessAsync()) return Forbid();
        await ProjectLifecycleRules.RequireActiveAsync(_context, ProjectId);
        if (!await CanAccessAsync()) return Forbid();
        if (UploadFile == null)
        {
            ModelState.AddModelError(nameof(UploadFile), "请选择要上传的资料文件");
            await LoadAsync();
            return Page();
        }
        if (UploadFile.Length <= 0 || UploadFile.Length > 50 * 1024 * 1024)
        {
            ModelState.AddModelError(nameof(UploadFile), "资料文件不能为空，且大小不能超过 50 MB");
            await LoadAsync();
            return Page();
        }

        try
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return RedirectToPage("/Account/Login");

            int? categoryId = null;
            string? categoryName = null;

            if (!string.IsNullOrWhiteSpace(UploadCustomCategory))
            {
                var trimmedName = UploadCustomCategory.Trim();
                var category = await _categories.GetOrCreateAsync(ProjectId, trimmedName);
                categoryId = category.Id;
                categoryName = category.Name;
                UploadCategory = ProjectDocumentCategory.Other;
            }
            else if (UploadCategoryId.HasValue)
            {
                var category = await _categories.GetByIdAsync(UploadCategoryId.Value);
                if (category != null)
                {
                    categoryId = category.Id;
                    categoryName = category.Name;
                    UploadCategory = ProjectDocumentCategory.Other;
                }
            }

            var document = await _documents.SaveWithCategoryAsync(
                ProjectId, user.Id, UploadFile, Description,
                category: UploadCategory,
                customCategoryId: categoryId,
                customCategoryName: categoryName);

            TempData["SuccessMessage"] = document.ConflictDetected
                ? $"资料已上传为 v{document.VersionNumber}，检测到同名文件内容变化"
                : $"资料已上传为 v{document.VersionNumber}";

            if (categoryId.HasValue)
            {
                TempData["SuccessMessage"] += $"，已自动分类为「{categoryName}」";
            }

            await _eventBus.PublishAsync(
                "project-document.version-created",
                new
                {
                    documentId = document.Id,
                    projectId = document.ProjectId,
                    document.FileName,
                    document.VersionNumber,
                    document.Category,
                    document.CategoryId,
                    document.CategoryName,
                    uploadedByUserId = user.Id
                },
                "ProjectDocument",
                document.Id.ToString());

            return RedirectToPage(new { projectId = ProjectId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload a document to project {ProjectId}", ProjectId);
            ModelState.AddModelError(string.Empty, "资料上传失败，请稍后重试");
            await LoadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSavePermissionAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null || !await CanManageAsync(user)) return Forbid();
        try
        {
            int? customCategoryId = PermissionCustomCategoryId;
            string categoryDisplayName = PermissionCategory.GetDisplayName();

            if (customCategoryId.HasValue)
            {
                var category = await _categories.GetByIdAsync(customCategoryId.Value);
                if (category != null)
                {
                    categoryDisplayName = category.Name;
                }
            }

            await _agentDocumentAccess.SetPermissionAsync(
                ProjectId, PermissionAgentKey, PermissionCategory,
                PermissionCanRead, PermissionCanWrite, user.Id,
                categoryId: customCategoryId);

            TempData["SuccessMessage"] = $"Agent 资料权限已更新：{categoryDisplayName}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update document permission in project {ProjectId}", ProjectId);
            TempData["ErrorMessage"] = "Agent 资料权限更新失败，请稍后重试";
        }
        return RedirectToPage(new { projectId = ProjectId, categoryFilter = CategoryFilter, categoryFilterId = CategoryFilterId });
    }

    public async Task<IActionResult> OnPostChangeCategoryAsync(int id, string category)
    {
        if (!await CanAccessAsync()) return Forbid();
        await ProjectLifecycleRules.RequireActiveAsync(_context, ProjectId);
        var user = await _userManager.GetUserAsync(User);
        if (user == null || !await CanManageAsync(user)) return Forbid();
        
        var document = await _context.ProjectDocuments.FirstOrDefaultAsync(item => item.Id == id && item.ProjectId == ProjectId);
        if (document == null) return NotFound();

        // 判断是预设分类还是自定义分类
        if (int.TryParse(category, out int customCategoryId))
        {
            // 自定义分类
            var customCat = await _categories.GetByIdAsync(customCategoryId);
            if (customCat == null) return BadRequest("自定义分类不存在");
            document.CategoryId = customCat.Id;
            document.CategoryName = customCat.Name;
            document.Category = ProjectDocumentCategory.Other;
            TempData["SuccessMessage"] = $"资料已归入\"{customCat.Name}\"";
        }
        else if (Enum.TryParse<ProjectDocumentCategory>(category, true, out var presetCategory))
        {
            // 预设分类
            document.Category = presetCategory;
            document.CategoryId = null;
            document.CategoryName = null;
            TempData["SuccessMessage"] = $"资料已归入\"{presetCategory.GetDisplayName()}\"";
        }
        else
        {
            return BadRequest("分类无效");
        }

        await _context.SaveChangesAsync();
        await _eventBus.PublishAsync(
            "project-document.category-changed",
            new
            {
                documentId = document.Id,
                projectId = document.ProjectId,
                document.FileName,
                document.Category,
                document.CategoryId,
                document.CategoryName,
                operatedByUserId = user.Id
            },
            "ProjectDocument",
            document.Id.ToString());
        return RedirectToPage(new { projectId = ProjectId, categoryFilter = CategoryFilter, categoryFilterId = CategoryFilterId });
    }

    public async Task<IActionResult> OnGetDownloadAsync(int id)
    {
        if (!await CanAccessAsync()) return Forbid();
        var result = await _documents.ResolveDownloadAsync(id, ProjectId);
        return result == null ? NotFound() : PhysicalFile(result.Value.FullPath, "application/octet-stream", result.Value.FileName);
    }

    public async Task<IActionResult> OnGetDownloadDraftAsync(int draftId)
    {
        if (!await CanAccessAsync()) return Forbid();
        
        var draft = await _context.AiDocumentDrafts
            .FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == ProjectId);
        
        if (draft == null) return NotFound();
        
        var fullPath = _documents.ResolveStoragePath(draft.DraftFilePath);
        
        if (!System.IO.File.Exists(fullPath)) return NotFound();
        
        var fileName = Path.GetFileName(draft.DraftFilePath);
        return PhysicalFile(fullPath, "application/octet-stream", fileName);
    }

    public async Task<JsonResult> OnGetVersionHistoryAsync(int documentId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return new JsonResult(new { success = false, message = "未登录" });
        if (!await CanAccessAsync()) return new JsonResult(new { success = false, message = "无权访问" });

        var history = await _documents.GetVersionHistoryAsync(ProjectId, documentId);
        var data = history.Select(h => new
        {
            id = h.Id,
            fileName = h.FileName,
            versionNumber = h.VersionNumber,
            fileSize = h.FileSize,
            description = h.Description,
            uploadedAt = h.UploadedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            isCurrent = h.IsCurrent
        }).ToList();

        return new JsonResult(new { success = true, data });
    }

    public async Task<IActionResult> OnPostRollbackAsync(int versionId)
    {
        if (!await CanAccessAsync()) return Forbid();
        await ProjectLifecycleRules.RequireActiveAsync(_context, ProjectId);
        if (!await CanAccessAsync()) return Forbid();
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");

        var result = await _documents.RollbackVersionAsync(ProjectId, versionId, user.Id);
        
        if (result == null)
        {
            TempData["ErrorMessage"] = "版本不存在";
        }
        else
        {
            TempData["SuccessMessage"] = $"已回滚至 v{result.VersionNumber}";
            await _eventBus.PublishAsync(
                "project-document.version-rolled-back",
                new { documentId = result.Id, projectId = result.ProjectId, result.FileName, result.VersionNumber, operatedByUserId = user.Id },
                "ProjectDocument",
                result.Id.ToString());
        }

        return RedirectToPage(new { projectId = ProjectId });
    }

    public async Task<IActionResult> OnPostReindexAsync(int documentId)
    {
        if (!await CanAccessAsync()) return Forbid();
        var documentExists = await _context.ProjectDocuments.AsNoTracking()
            .AnyAsync(document => document.Id == documentId
                && document.ProjectId == ProjectId
                && document.IsCurrent);
        if (!documentExists)
        {
            TempData["ErrorMessage"] = "资料不存在或不是当前版本";
            return RedirectToPage(new { projectId = ProjectId });
        }

        await _documentIndex.ReindexAsync(documentId, HttpContext.RequestAborted);
        TempData["SuccessMessage"] = "已加入知识索引队列";
        return RedirectToPage(new { projectId = ProjectId });
    }

    public async Task<IActionResult> OnPostAiReviseAsync(int documentId, string requirement)
    {
        if (!await CanAccessAsync()) return Forbid();
        await ProjectLifecycleRules.RequireActiveAsync(_context, ProjectId);
        if (!await CanAccessAsync()) return Forbid();
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");

        try
        {
            var document = await _context.ProjectDocuments
                .FirstOrDefaultAsync(d => d.Id == documentId && d.ProjectId == ProjectId);
            
            if (document == null)
            {
                TempData["ErrorMessage"] = "文档不存在";
                return RedirectToPage(new { projectId = ProjectId });
            }

            // 检查是否有写入权限（任意Agent有写入权限即可）
            var permissions = await _agentDocumentAccess.GetPermissionsAsync(ProjectId);
            var writePermission = permissions.FirstOrDefault(p =>
                p.CanWrite &&
                ((p.CategoryId.HasValue && document.CategoryId.HasValue && p.CategoryId == document.CategoryId) ||
                 (!p.CategoryId.HasValue && !document.CategoryId.HasValue && p.Category == document.Category)));

            if (writePermission == null)
            {
                TempData["ErrorMessage"] = "该文档所属分类未配置Agent写入权限";
                return RedirectToPage(new { projectId = ProjectId });
            }

            // 获取执行修改的Agent信息（使用有权限的Agent）
            var agentKey = writePermission.AgentKey;
            var allAgents = await _agentRegistry.GetAllAsync();
            var agentInfo = allAgents.FirstOrDefault(a => a.AgentKey == agentKey);
            var agentDisplayName = agentInfo?.Name ?? agentKey;

            // 读取原文档内容（支持所有格式预览）
            var fullPath = _documents.ResolveStoragePath(document.StoragePath);
            string originalContent = await DocParser.ParseAsync(fullPath);

            // 调用AI进行修改
            var prompt = $"请根据以下要求修改文档内容：\n\n修改要求：{requirement}\n\n原文档内容：\n{originalContent}\n\n请直接返回修改后的完整内容，不要添加任何解释说明。";
            var revisedContent = await _aiService.GetChatCompletionAsync(prompt);

            // 保存草稿文件
            var newFileName = Path.GetFileNameWithoutExtension(document.FileName) + "_AI修订_" + AppTime.Now.ToString("yyyyMMddHHmmss") + ".md";
            var draftStoragePath = await _documents.SaveDraftTextAsync(ProjectId, newFileName, revisedContent);

            // 创建草稿记录
            var draft = new AiDocumentDraft
            {
                ProjectId = ProjectId,
                SourceDocumentId = document.Id,
                DraftFileName = document.FileName,
                DraftFilePath = draftStoragePath,
                OriginalContent = originalContent,
                RevisedContent = revisedContent,
                ChangeDescription = $"AI修订：{requirement}",
                AgentKey = agentKey,
                AgentDisplayName = agentDisplayName,
                AuditStatus = DraftAuditStatus.Pending,
                ExpireTime = AppTime.Now.AddDays(30),
                CreatedAt = AppTime.Now
            };
            
            _context.AiDocumentDrafts.Add(draft);
            await _context.SaveChangesAsync();

            TempData["SuccessMessage"] = "AI修改完成，已生成草稿，等待人工审核";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to revise document {DocumentId} with AI in project {ProjectId}", documentId, ProjectId);
            TempData["ErrorMessage"] = "AI修改失败，请稍后重试";
        }

        return RedirectToPage(new { projectId = ProjectId });
    }

    public async Task<IActionResult> OnPostAuditDraftAsync(int draftId, string action)
    {
        if (!await CanAccessAsync()) return Forbid();
        await ProjectLifecycleRules.RequireActiveAsync(_context, ProjectId);
        if (!await CanAccessAsync()) return Forbid();
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return RedirectToPage("/Account/Login");

        try
        {
            var draft = await _context.AiDocumentDrafts
                .FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == ProjectId);
            
            if (draft == null)
            {
                TempData["ErrorMessage"] = "草稿不存在";
                return RedirectToPage(new { projectId = ProjectId });
            }

            if (draft.AuditStatus != DraftAuditStatus.Pending)
            {
                TempData["ErrorMessage"] = "该草稿已被审核";
                return RedirectToPage(new { projectId = ProjectId });
            }

            if (action == "pass")
            {
                // 审核通过，创建新版本
                var document = await _context.ProjectDocuments
                    .FirstOrDefaultAsync(d => d.Id == draft.SourceDocumentId);
                
                if (document == null)
                {
                    TempData["ErrorMessage"] = "原文档不存在";
                    return RedirectToPage(new { projectId = ProjectId });
                }

                // 获取草稿文件路径
                var draftFullPath = _documents.ResolveStoragePath(draft.DraftFilePath);
                
                // 第一步：更新原文档版本状态
                if (document.IsCurrent)
                {
                    document.IsCurrent = false;
                }
                await _context.SaveChangesAsync();

                // 第二步：创建新版本记录
                var newDocument = new ProjectDocument
                {
                    ProjectId = ProjectId,
                    UploadedById = user.Id,
                    Category = document.Category,
                    CategoryId = document.CategoryId,
                    CategoryName = document.CategoryName,
                    FileName = document.FileName,
                    StoragePath = draft.DraftFilePath,
                    FileHash = null,
                    FileSize = System.IO.File.Exists(draftFullPath) ? new FileInfo(draftFullPath).Length : 0,
                    VersionNumber = document.VersionNumber + 1,
                    IsCurrent = true,
                    Description = draft.ChangeDescription,
                    UploadedAt = AppTime.Now
                };
                
                _context.ProjectDocuments.Add(newDocument);
                await _context.SaveChangesAsync();

                // 第三步：更新草稿状态
                draft.AuditStatus = DraftAuditStatus.Pass;
                draft.AuditUserId = user.Id.ToString();
                draft.AuditTime = AppTime.Now;
                _context.AiDocumentDrafts.Update(draft);
                await _context.SaveChangesAsync();

                TempData["SuccessMessage"] = $"草稿已通过，已创建新版本 v{newDocument.VersionNumber}";
                await _eventBus.PublishAsync(
                    "project-document.version-created",
                    new
                    {
                        documentId = newDocument.Id,
                        projectId = newDocument.ProjectId,
                        newDocument.FileName,
                        newDocument.VersionNumber,
                        source = "ai-draft-approved",
                        draftId = draft.Id,
                        operatedByUserId = user.Id
                    },
                    "ProjectDocument",
                    newDocument.Id.ToString());
            }
            else if (action == "reject")
            {
                // 审核驳回
                draft.AuditStatus = DraftAuditStatus.Reject;
                draft.AuditUserId = user.Id.ToString();
                draft.AuditTime = AppTime.Now;
                _context.AiDocumentDrafts.Update(draft);
                await _context.SaveChangesAsync();

                TempData["SuccessMessage"] = "草稿已驳回";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to audit document draft {DraftId} in project {ProjectId}", draftId, ProjectId);
            TempData["ErrorMessage"] = "审核失败，请稍后重试";
        }

        return RedirectToPage(new { projectId = ProjectId });
    }

    public async Task<JsonResult> OnGetDraftContentAsync(int draftId)
    {
        if (!await CanAccessAsync())
        {
            return new JsonResult(new { error = "无权访问" })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }

        var draft = await _context.AiDocumentDrafts
            .FirstOrDefaultAsync(d => d.Id == draftId && d.ProjectId == ProjectId);
        
        if (draft == null)
        {
            return new JsonResult(new { error = "草稿不存在" });
        }

        return new JsonResult(new
        {
            id = draft.Id,
            fileName = draft.DraftFileName,
            originalContent = draft.OriginalContent,
            revisedContent = draft.RevisedContent,
            changeDescription = draft.ChangeDescription,
            agentDisplayName = draft.AgentDisplayName,
            auditStatus = draft.AuditStatus.ToString(),
            createdAt = draft.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
        });
    }

    public async Task<JsonResult> OnGetDocumentContentAsync(int documentId)
    {
        if (!await CanAccessAsync())
        {
            return new JsonResult(new { success = false, error = "无权访问" })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }

        var document = await _context.ProjectDocuments
            .FirstOrDefaultAsync(d => d.Id == documentId && d.ProjectId == ProjectId);
        
        if (document == null)
        {
            return new JsonResult(new { success = false, error = "文档不存在" });
        }

        var fullPath = _documents.ResolveStoragePath(document.StoragePath);
        string content = await DocParser.ParseAsync(fullPath);

        return new JsonResult(new
        {
            success = true,
            id = document.Id,
            fileName = document.FileName,
            content = content,
            versionNumber = document.VersionNumber,
            description = document.Description
        });
    }

    public async Task<IActionResult> OnPostAddCustomCategoryAsync(string name)
    {
        if (!await CanAccessAsync()) return Forbid();
        await ProjectLifecycleRules.RequireActiveAsync(_context, ProjectId);
        var user = await _userManager.GetUserAsync(User);
        if (user == null || !await CanManageAsync(user)) return Forbid();
        try
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                TempData["ErrorMessage"] = "分类名称不能为空";
            }
            else
            {
                var category = await _categories.GetOrCreateAsync(ProjectId, name.Trim());
                TempData["SuccessMessage"] = $"分类「{category.Name}」已创建";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add a document category in project {ProjectId}", ProjectId);
            TempData["ErrorMessage"] = "分类创建失败，请稍后重试";
        }
        return RedirectToPage(new { projectId = ProjectId });
    }

    public async Task<IActionResult> OnPostDeleteCategoryAsync(int id)
    {
        if (!await CanAccessAsync()) return Forbid();
        await ProjectLifecycleRules.RequireActiveAsync(_context, ProjectId);
        var user = await _userManager.GetUserAsync(User);
        if (user == null || !await CanManageAsync(user)) return Forbid();
        try
        {
            var category = await _categories.GetByIdAsync(id);
            if (category == null)
            {
                TempData["ErrorMessage"] = "分类不存在";
            }
            else
            {
                var inUse = await _context.ProjectDocuments.AnyAsync(d => d.ProjectId == ProjectId && d.CategoryId == id);
                if (inUse)
                {
                    TempData["ErrorMessage"] = "该分类下还有资料，无法删除";
                }
                else
                {
                    await _categories.DeleteAsync(id);
                    TempData["SuccessMessage"] = $"分类「{category.Name}」已删除";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete document category {CategoryId} in project {ProjectId}", id, ProjectId);
            TempData["ErrorMessage"] = "分类删除失败，请稍后重试";
        }
        return RedirectToPage(new { projectId = ProjectId });
    }

    private Task<bool> ProjectExistsAsync() => _context.Project.AnyAsync(p => p.Id == ProjectId && !p.IsDeleted);

    private async Task<bool> CanAccessAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return false;
        if (user.Role == UserRole.systemAdmin) return true;
        return await _context.Project.AnyAsync(project => project.Id == ProjectId
            && !project.IsDeleted
            && (project.LeaderUserId == user.Id
                || _context.ProjectUsers.Any(member => member.ProjectId == ProjectId && member.UserId == user.Id)));
    }

    private async Task LoadAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        ProjectName = await _context.Project.Where(p => p.Id == ProjectId && !p.IsDeleted).Select(p => p.Name).FirstOrDefaultAsync() ?? string.Empty;
        IsReadOnly = !await _context.Project.AnyAsync(p => p.Id == ProjectId && !p.IsDeleted && p.Status == ProjectStatus.Active);
        Documents = await _documents.GetDocumentsAsync(ProjectId);
        ProjectCategories = await _categories.GetCategoriesAsync(ProjectId);

        // 解析分类筛选条件
        if (!string.IsNullOrWhiteSpace(CategoryFilter))
        {
            // 尝试解析为预设分类枚举
            if (Enum.TryParse<ProjectDocumentCategory>(CategoryFilter, true, out var presetCat))
            {
                PresetCategoryFilter = presetCat;
                Documents = Documents.Where(item => item.Category == presetCat && !item.CategoryId.HasValue).ToList();
            }
            // 尝试解析为自定义分类ID
            else if (int.TryParse(CategoryFilter, out int customCatId))
            {
                CategoryFilterId = customCatId;
                Documents = Documents.Where(item => item.CategoryId == customCatId).ToList();
            }
        }
        Agents = await _agentRegistry.GetAllAsync();
        AgentPermissions = await _agentDocumentAccess.GetPermissionsAsync(ProjectId);
        CanManageAgentPermissions = user != null && await CanManageAsync(user);

        // 获取预设分类名称列表
        PresetCategoryNames = string.Join(",", Enum.GetValues<ProjectDocumentCategory>()
            .Select(c => c.GetDisplayName()));

        PermissionViewList = new List<(AgentDocumentPermission, string, string)>();
        foreach (var permission in AgentPermissions)
        {
            string agentDisplayName = Agents.FirstOrDefault(a => a.AgentKey == permission.AgentKey)?.Name ?? permission.AgentKey;
            string categoryDisplayName = permission.Category.GetDisplayName();
            if (permission.CategoryId.HasValue)
            {
                var cat = ProjectCategories.FirstOrDefault(c => c.Id == permission.CategoryId);
                if (cat != null) categoryDisplayName = cat.Name;
            }
            PermissionViewList.Add((permission, agentDisplayName, categoryDisplayName));
        }

        // 计算每个文档是否有Agent写入权限
        DocumentWritePermissions = new Dictionary<int, bool>();
        foreach (var doc in Documents)
        {
            bool hasWritePermission;
            if (doc.CategoryId.HasValue)
            {
                // 自定义分类：只检查针对该自定义分类的权限
                hasWritePermission = AgentPermissions.Any(p =>
                    p.CanWrite && p.CategoryId == doc.CategoryId.Value);
            }
            else
            {
                // 预设分类：检查针对该预设分类的权限（不含自定义分类）
                hasWritePermission = AgentPermissions.Any(p =>
                    p.CanWrite && p.Category == doc.Category && !p.CategoryId.HasValue);
            }
            DocumentWritePermissions[doc.Id] = hasWritePermission;
        }

        // 加载AI草稿列表
        PendingDrafts = await _context.AiDocumentDrafts
            .Where(d => d.ProjectId == ProjectId && d.AuditStatus == DraftAuditStatus.Pending)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();

        ProcessedDrafts = await _context.AiDocumentDrafts
            .Where(d => d.ProjectId == ProjectId && d.AuditStatus != DraftAuditStatus.Pending)
            .OrderByDescending(d => d.CreatedAt)
            .Take(50)
            .ToListAsync();
    }

    private async Task<bool> CanManageAsync(ApplicationUser user)
    {
        if (user.Role == UserRole.systemAdmin) return true;
        if (await _context.Project.AnyAsync(item => item.Id == ProjectId && item.LeaderUserId == user.Id)) return true;
        return await _context.ProjectUsers.AnyAsync(item => item.ProjectId == ProjectId
            && item.UserId == user.Id
            && item.ProjectRole == (int)ProjectRole.Admin);
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain
{
    public class DailyReportService : IDailyReportService
    {
        private const long MaxAttachmentBytes = 10 * 1024 * 1024;
        private const int MaxAttachmentCount = 5;
        private static readonly IReadOnlyDictionary<string, string> AllowedAttachmentTypes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".pdf"] = "application/pdf",
                [".doc"] = "application/msword",
                [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                [".xls"] = "application/vnd.ms-excel",
                [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                [".ppt"] = "application/vnd.ms-powerpoint",
                [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                [".jpg"] = "image/jpeg",
                [".jpeg"] = "image/jpeg",
                [".png"] = "image/png",
                [".txt"] = "text/plain"
            };
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public DailyReportService(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        // 创建相关
        public async Task<List<DomainSelectListItem>> GetAccessibleProjects(ApplicationUser currentUser, bool activeOnly = false)
        {
            return await ApplyProjectAccess(_context.Project.AsNoTracking().Where(p => !p.IsDeleted && (!activeOnly || p.Status == ProjectStatus.Active)), currentUser)
               .OrderBy(p => p.Name)
               .Select(p => new DomainSelectListItem(p.Name, p.Id.ToString()))
               .ToListAsync();
        }

        public Task<bool> CanAccessProjectAsync(int projectId, ApplicationUser currentUser)
        {
            return ApplyProjectAccess(
                    _context.Project.AsNoTracking().Where(project => project.Id == projectId && !project.IsDeleted),
                    currentUser)
                .AnyAsync();
        }

        public Task<bool> CanAccessReportAsync(int reportId, ApplicationUser currentUser)
        {
            return ApplyReportAccess(
                    _context.DailyReport.AsNoTracking().Where(report => report.Id == reportId
                        && !report.IsDeleted
                        && report.Project != null
                        && !report.Project.IsDeleted),
                    currentUser)
                .AnyAsync();
        }

        public async Task<Project?> GetDailyReportProject(int projectId)
        {
            return await _context.Project
                .FirstOrDefaultAsync(p => p.Id == projectId && !p.IsDeleted);
        }

        public async Task<(bool HasPermission, string ErrorMessage)> CheckReportPermission(ApplicationUser user, Project project)
        {
            if (project == null)
                return (false, "项目信息不存在");

            if (project.Status != ProjectStatus.Active) return (false, ProjectLifecycleRules.ReadOnlyMessage);
            if (await CanAccessProjectAsync(project.Id, user))
                return (true, string.Empty);

            return project.IsEncrypted == '2'
                ? (false, $"「{project.Name}」为私密项目，仅项目负责人或项目管理员可提交报告")
                : (false, $"你不是「{project.Name}」的项目成员，无法提交报告");
        }

        public async Task SaveDailyReport(DailyReport dailyReport, List<IFormFile> attachments, string webRootPath)
        {
            ValidateAttachments(attachments);
            var writtenFiles = new List<string>();
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // 标题非空及非全空格验证
                if (string.IsNullOrWhiteSpace(dailyReport.ReportTitle))
                {
                    throw new ArgumentException("报告标题不能为空，且不能为空白（包括全空格）");
                }
                var project = await _context.Project.FindAsync(dailyReport.ProjectId)
                    ?? throw new InvalidOperationException("项目不存在或已被删除");
                if (project.IsDeleted) throw new InvalidOperationException("项目不存在或已被删除");
                dailyReport.Project = project;

                _context.DailyReport.Add(dailyReport);
                await _context.SaveChangesAsync();

                var reporter = await _context.Users.FindAsync(dailyReport.ReporterId);
                string reporterName = reporter?.RealName ?? reporter?.UserName ?? "未知用户";

                List<string> addedAttachmentNames = new();
                if (attachments != null && attachments.Any())
                {
                    var uploadDir = Path.Combine(
                        webRootPath,
                        "uploads",
                        "reports",
                        dailyReport.ProjectId.ToString(),
                        AppTime.Today.ToString("yyyyMMdd")
                    );
                    Directory.CreateDirectory(uploadDir);

                    var reportAttachments = new List<DailyReportAttachment>();
                    foreach (var file in attachments)
                    {
                        var (originalName, ext, contentType) = ValidateAttachment(file);
                        var fileName = $"{Guid.NewGuid()}{ext}";
                        var filePath = Path.Combine(uploadDir, fileName);

                        using (var stream = new FileStream(filePath, FileMode.Create))
                        {
                            await file.CopyToAsync(stream);
                        }
                        writtenFiles.Add(filePath);

                        var attachment = new DailyReportAttachment
                        {
                            FileName = originalName,
                            FilePath = Path.Combine("uploads", "reports", dailyReport.ProjectId.ToString(), AppTime.Today.ToString("yyyyMMdd"), fileName),
                            ContentType = contentType,
                            FileSize = file.Length,
                            UploadedAt = AppTime.Now,
                            DailyReportId = dailyReport.Id,
                            UploadedByUserId = dailyReport.ReporterId,
                            DailyReport = dailyReport
                        };
                        reportAttachments.Add(attachment);
                        addedAttachmentNames.Add(originalName);
                    }

                    _context.DailyReportAttachments.AddRange(reportAttachments);
                    await _context.SaveChangesAsync();
                }

                var createLog = new ChangeLog
                {
                    OperationType = OperationType.创建,
                    OperationStatus = OperationStatus.成功,
                    OperatedAt = DateTime.UtcNow,
                    OperationTarget = OperationTarget.日报,
                    OperatedByUserId = dailyReport.ReporterId,
                    OperatedByUserName = reporter?.UserName ?? "未知用户",
                    OperatedByRealName = reporterName,
                    ProjectId = dailyReport.ProjectId,
                    ProjectName = project?.Name ?? "未知项目",
                    TargetId = dailyReport.Id,
                    TargetName = dailyReport.ReportTitle,
                    AfterContent = $"上报人：{reporterName}\n" +
                                  $"标题：{dailyReport.ReportTitle}\n" +
                                  $"报告类型：{GetReportTypeName(dailyReport.ReportType)}\n" +
                                  $"报告日期：{dailyReport.ReportDate:yyyy-MM-dd}\n" +
                                  $"创建时间：{dailyReport.CreatedAt:yyyy-MM-dd HH:mm}\n" +
                                  (addedAttachmentNames.Any() ? $"附件：{string.Join("、", addedAttachmentNames)}" : "")
                };
                _context.ChangeLogs.Add(createLog);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                DeleteWrittenFiles(writtenFiles);
                throw;
            }
        }

        // 详情相关
        public async Task<DailyReport?> GetDailyReportWithDetails(int id, ApplicationUser currentUser)
        {
            var query = _context.DailyReport
               .Include(dr => dr.Project)
               .Include(dr => dr.Attachments.Where(a => !a.IsDeleted))
               .Where(dr => dr.Id == id && !dr.IsDeleted && dr.Project != null && !dr.Project.IsDeleted);
            return await ApplyReportAccess(query, currentUser).FirstOrDefaultAsync();
        }

        public async Task<string> GetReporterName(int reporterId)
        {
            var reporter = await _context.Users.FindAsync(reporterId);
            return reporter?.RealName ?? reporter?.UserName ?? "未知用户";
        }

        public async Task<List<AttachmentViewModel>> GetAttachmentsByReportId(int reportId)
        {
            return await _context.DailyReportAttachments
                .AsNoTracking() // 禁用跟踪，确保获取最新数据
                .Where(a => a.DailyReportId == reportId && !a.IsDeleted)
                .Select(a => new AttachmentViewModel
                {
                    Id = a.Id,
                    FileName = a.FileName,
                    FilePath = a.FilePath,
                    FileSize = a.FileSize,
                    FileSizeFormatted = FormatFileSize(a.FileSize),
                    UploadedAt = a.UploadedAt,
                    IconClass = GetFileIconClass(a.FileName)
                })
                .ToListAsync();
        }

        // 编辑相关
        public async Task<DailyReport?> GetOriginalDailyReport(int id)
        {
            return await _context.DailyReport
               .Include(dr => dr.Attachments.Where(a => !a.IsDeleted)) // 加载时过滤已删除附件
               .Include(dr => dr.Project)
               .FirstOrDefaultAsync(dr => dr.Id == id && !dr.IsDeleted);
        }

        public async Task<List<AttachmentViewModel>> GetActiveAttachmentsForEdit(int reportId)
        {
            // 直接查询数据库，不依赖导航属性
            return await _context.DailyReportAttachments
                .AsNoTracking()
                .Where(a => a.DailyReportId == reportId && !a.IsDeleted)
                .Select(a => new AttachmentViewModel
                {
                    Id = a.Id,
                    FileName = a.FileName,
                    FilePath = a.FilePath,
                    FileSize = a.FileSize,
                    FileSizeFormatted = FormatFileSize(a.FileSize),
                    UploadedAt = a.UploadedAt,
                    IconClass = GetFileIconClass(a.FileName)
                })
                .ToListAsync();
        }

        // 1. 编辑页权限校验方法（CheckEditPermission）
        public async Task<(bool CanEdit, string ErrorMessage)> CheckEditPermission(DailyReport original, ApplicationUser currentUser)
        {
            if (original == null)
                return (false, "日报信息不存在");

            if (!await CanAccessProjectAsync(original.ProjectId, currentUser))
                return (false, "你没有该项目的日报访问权限");

            if (!await _context.Project.AnyAsync(p => p.Id == original.ProjectId && !p.IsDeleted && p.Status == ProjectStatus.Active))
                return (false, ProjectLifecycleRules.ReadOnlyMessage);

            // 仅保留创建人判断
            bool isCreator = original.ReporterId == currentUser.Id;
            bool within24Hours = (AppTime.Now - original.CreatedAt) <= TimeSpan.FromHours(24);

            // 只有创建人且在24小时内可编辑
            if (isCreator && within24Hours)
                return (true, string.Empty);

            // 无权限提示（区分原因）
            if (!isCreator)
                return (false, "仅创建者可编辑项目日报");

            return (false, "项目日报发布已超过24小时，无法编辑");
        }


        public async Task DeleteAttachments(List<int> deletedIds, DailyReport original, string webRootPath, ApplicationUser currentUser)
        {
            if (!deletedIds.Any() || original == null) return;

            var physicalFilesToDelete = new List<string>();
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // 从数据库查询要删除的附件（不依赖内存导航属性）
                var attachmentsToDelete = await _context.DailyReportAttachments
                    .Where(att => deletedIds.Contains(att.Id)
                                && att.DailyReportId == original.Id
                                && !att.IsDeleted)
                    .ToListAsync();

                if (!attachmentsToDelete.Any()) return;

                // 记录删除的附件名
                List<string> deletedFileNames = attachmentsToDelete.Select(att => att.FileName).ToList();
                string reporterName = await GetReporterName(original.ReporterId);

                // 执行删除操作
                foreach (var att in attachmentsToDelete)
                {
                    // 物理删除文件
                    if (!string.IsNullOrEmpty(att.FilePath))
                    {
                        var reportRoot = Path.GetFullPath(Path.Combine(webRootPath, "uploads", "reports"));
                        var fullPath = Path.GetFullPath(Path.Combine(webRootPath, att.FilePath));
                        if (fullPath.StartsWith(reportRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(fullPath))
                        {
                            physicalFilesToDelete.Add(fullPath);
                        }
                    }

                    // 逻辑删除标记
                    att.IsDeleted = true;
                    att.LastModifiedAt = AppTime.Now;
                    _context.DailyReportAttachments.Update(att);
                }

                await _context.SaveChangesAsync();

                // 记录删除日志
                var deleteLog = new ChangeLog
                {
                    OperationType = OperationType.删除,
                    OperationStatus = OperationStatus.成功,
                    OperatedAt = DateTime.UtcNow,
                    OperationTarget = OperationTarget.日报,
                    OperatedByUserId = currentUser.Id,
                    OperatedByUserName = currentUser.UserName,
                    OperatedByRealName = currentUser.RealName,
                    ProjectId = original.ProjectId,
                    ProjectName = original.Project?.Name ?? "未知项目",
                    TargetId = original.Id,
                    TargetName = original.ReportTitle,
                    BeforeContent = $"删除附件：{string.Join("、", deletedFileNames)}\n上报人：{reporterName}"
                };
                _context.ChangeLogs.Add(deleteLog);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();
                DeleteWrittenFiles(physicalFilesToDelete);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task AddNewAttachments(List<IFormFile> newAttachments, DailyReport original, string webRootPath, ApplicationUser currentUser)
        {
            if (newAttachments == null || !newAttachments.Any() || original == null) return;

            ValidateAttachments(newAttachments);
            var activeAttachmentCount = await _context.DailyReportAttachments
                .CountAsync(attachment => attachment.DailyReportId == original.Id && !attachment.IsDeleted);
            if (activeAttachmentCount + newAttachments.Count > MaxAttachmentCount)
                throw new InvalidOperationException($"附件总数不能超过 {MaxAttachmentCount} 个");

            var writtenFiles = new List<string>();
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var projectId = original.ProjectId.ToString();
                var dateDir = AppTime.Today.ToString("yyyyMMdd");
                var uploadDir = Path.Combine(webRootPath ?? "", "uploads", "reports", projectId, dateDir);
                Directory.CreateDirectory(uploadDir);

                List<string> addedFileNames = new();

                foreach (var file in newAttachments)
                {
                    var (originalName, ext, contentType) = ValidateAttachment(file);
                    var fileName = $"{Guid.NewGuid()}{ext}";
                    var filePath = Path.Combine(uploadDir, fileName);

                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }
                    writtenFiles.Add(filePath);

                    original.Attachments.Add(new DailyReportAttachment
                    {
                        FileName = originalName,
                        FilePath = Path.Combine("uploads", "reports", projectId, dateDir, fileName).Replace("\\", "/"),
                        ContentType = contentType,
                        FileSize = file.Length,
                        UploadedAt = AppTime.Now,
                        DailyReportId = original.Id,
                        UploadedByUserId = currentUser.Id,
                        DailyReport = original
                    });
                    addedFileNames.Add(originalName);
                }

                await _context.SaveChangesAsync();

                string reporterName = await GetReporterName(original.ReporterId);
                var attachmentLog = new ChangeLog
                {
                    OperationType = OperationType.更新,
                    OperationStatus = OperationStatus.成功,
                    OperatedAt = DateTime.UtcNow,
                    OperationTarget = OperationTarget.日报,
                    OperatedByUserId = currentUser.Id,
                    OperatedByUserName = currentUser.UserName,
                    OperatedByRealName = currentUser.RealName,
                    ProjectId = original.ProjectId,
                    ProjectName = original.Project?.Name ?? "未知项目",
                    TargetId = original.Id,
                    TargetName = original.ReportTitle,
                    AfterContent =   $"新增附件：{string.Join("、", addedFileNames)}\n" +
                                $"上报人：{reporterName}"
                };
                _context.ChangeLogs.Add(attachmentLog);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                DeleteWrittenFiles(writtenFiles);
                throw;
            }
        }

        private static void ValidateAttachments(IReadOnlyCollection<IFormFile>? files)
        {
            if (files == null || files.Count == 0) return;
            if (files.Count > MaxAttachmentCount)
                throw new InvalidOperationException($"最多上传 {MaxAttachmentCount} 个附件");
            foreach (var file in files) ValidateAttachment(file);
        }

        private static (string OriginalName, string Extension, string ContentType) ValidateAttachment(IFormFile file)
        {
            if (file == null || file.Length <= 0)
                throw new InvalidOperationException("附件不能为空");
            if (file.Length > MaxAttachmentBytes)
                throw new InvalidOperationException("单个附件不能超过 10 MB");

            var originalName = Path.GetFileName(file.FileName ?? string.Empty);
            var extension = Path.GetExtension(originalName).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(originalName) || !AllowedAttachmentTypes.TryGetValue(extension, out var contentType))
                throw new InvalidOperationException("附件格式不支持");
            return (originalName, extension, contentType);
        }

        private static void DeleteWrittenFiles(IEnumerable<string> files)
        {
            foreach (var file in files)
            {
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                }
                catch
                {
                    // Best-effort cleanup; preserve the original exception.
                }
            }
        }

        public async Task UpdateDailyReport(DailyReport original, string title, string content, int reportType, DateTime reportDate, ApplicationUser currentUser)
        {
            if (original == null)
                throw new Exception("日报信息不存在");

            bool hasTitleChange = original.ReportTitle != title?.Trim();
            bool hasContentChange = original.ReportContent != content?.Trim();
            bool hasTypeChange = original.ReportType != reportType;
            bool hasDateChange = original.ReportDate != reportDate;

            if (!hasTitleChange && !hasContentChange && !hasTypeChange && !hasDateChange)
                return;

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                string oldTitle = original.ReportTitle;
                // 不再保存完整的旧内容，仅用于判断变更
                int oldReportType = original.ReportType;
                DateTime oldReportDate = original.ReportDate;

                original.ReportTitle = title?.Trim() ?? "";
                original.ReportContent = content?.Trim() ?? "";
                original.ReportType = reportType;
                original.ReportDate = reportDate;
                original.LastModifiedAt = AppTime.Now;

                _context.Update(original);
                await _context.SaveChangesAsync();

                string reporterName = await GetReporterName(original.ReporterId);

                List<string> beforeChanges = new();
                List<string> afterChanges = new();

                if (hasTitleChange)
                {
                    beforeChanges.Add($"标题：{oldTitle}");
                    afterChanges.Add($"标题：{original.ReportTitle}");
                }

                if (hasContentChange)
                {
                    // 内容变更时只显示提示，不展示具体内容
                    beforeChanges.Add("内容：（原内容）");
                    afterChanges.Add("内容：已修改");
                }

                if (hasTypeChange)
                {
                    beforeChanges.Add($"报告类型：{GetReportTypeName(oldReportType)}");
                    afterChanges.Add($"报告类型：{GetReportTypeName(original.ReportType)}");
                }

                if (hasDateChange)
                {
                    beforeChanges.Add($"报告日期：{oldReportDate:yyyy-MM-dd}");
                    afterChanges.Add($"报告日期：{original.ReportDate:yyyy-MM-dd}");
                }

                var updateLog = new ChangeLog
                {
                    OperationType = OperationType.更新,
                    OperationStatus = OperationStatus.成功,
                    OperatedAt = DateTime.UtcNow,
                    OperationTarget = OperationTarget.日报,
                    OperatedByUserId = currentUser.Id,
                    OperatedByUserName = currentUser.UserName,
                    OperatedByRealName = currentUser.RealName,
                    ProjectId = original.ProjectId,
                    ProjectName = original.Project?.Name ?? "未知项目",
                    TargetId = original.Id,
                    TargetName = original.ReportTitle,
                    BeforeContent = string.Join("\n", beforeChanges) + "\n" + $"上报人： {reporterName} \n",
                    AfterContent = string.Join("\n", afterChanges) + "\n" + $"上报人： {reporterName} \n",
                };
                _context.ChangeLogs.Add(updateLog);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        // 列表相关
        // 列表相关：添加关键词筛选逻辑
        public async Task<(List<DailyReport> Items, int TotalCount)> GetFilteredDailyReports(
            int? filterProjectId, int? filterReportType, string filterReporter, string filterKeyword, // 接收关键词参数
            DateTime? filterStartDate, DateTime? filterEndDate,
            int currentPage, int pageSize,
            ApplicationUser currentUser,
            bool excludeTeamReport = false)
        {
            var query = _context.DailyReport
               .Include(dr => dr.Project)
               .Where(dr => !dr.IsDeleted && dr.Project != null && !dr.Project.IsDeleted)
               .AsQueryable();
            query = ApplyReportAccess(query, currentUser);

            // 排除团队汇报（项目日报Tab使用）
            if (excludeTeamReport)
                query = query.Where(dr => dr.ReportType != 4);

            if (filterProjectId.HasValue)
                query = query.Where(dr => dr.ProjectId == filterProjectId.Value);
            if (filterReportType.HasValue)
                query = query.Where(dr => dr.ReportType == filterReportType.Value);
            if (!string.IsNullOrEmpty(filterReporter))
            {
                var matchingUserIds = await _context.Users
                   .Where(u => (u.UserName != null && u.UserName.Contains(filterReporter))
                       || (u.RealName != null && u.RealName.Contains(filterReporter)))
                   .Select(u => u.Id)
                   .ToListAsync();
                query = query.Where(dr => matchingUserIds.Contains(dr.ReporterId));
            }
            // 新增：内容关键词筛选（基于 ReportContent 字段）
            if (!string.IsNullOrEmpty(filterKeyword))
            {
                query = query.Where(dr => dr.ReportContent.Contains(filterKeyword));
            }
            if (filterStartDate.HasValue)
                query = query.Where(dr => dr.ReportDate >= filterStartDate.Value);
            if (filterEndDate.HasValue)
                query = query.Where(dr => dr.ReportDate <= filterEndDate.Value.AddDays(1).AddTicks(-1));

            var totalCount = await query.CountAsync();
            var items = await query
               .OrderByDescending(dr => dr.ReportDate)
               .Skip((currentPage - 1) * pageSize)
               .Take(pageSize)
               .ToListAsync();

            return (items, totalCount);
        }


        public async Task<Dictionary<int, bool>> GetCanDeletePermissions(List<DailyReport> items, ApplicationUser currentUser)
        {
            var result = new Dictionary<int, bool>();
            foreach (var item in items)
            {
                bool within24Hours = (AppTime.Now - item.CreatedAt) <= TimeSpan.FromHours(24);
                bool isCreator = item.ReporterId == currentUser.Id;
                bool isProjectAdmin = await _context.ProjectUsers
                   .AnyAsync(pu => pu.ProjectId == item.ProjectId && pu.UserId == currentUser.Id && pu.ProjectRole == (int)ProjectRole.Admin);
                bool isProjectLeader = item.Project?.LeaderUserId == currentUser.Id
                    || await _context.Project.AnyAsync(project => project.Id == item.ProjectId && project.LeaderUserId == currentUser.Id);
                bool isSystemAdmin = currentUser.Role == UserRole.systemAdmin;

                result[item.Id] = within24Hours && (isCreator || isProjectLeader || isProjectAdmin || isSystemAdmin);
            }
            return result;
        }

        public async Task<(bool Success, string Message)> DeleteDailyReport(int id, ApplicationUser currentUser)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var dailyReport = await _context.DailyReport
                   .Include(dr => dr.Project)
                   .Include(dr => dr.Attachments)
                   .FirstOrDefaultAsync(dr => dr.Id == id && !dr.IsDeleted);

                if (dailyReport == null)
                    return (false, "项目日报不存在或已被删除");

                if (dailyReport.Project.IsDeleted)
                    return (false, "所属项目已删除，无法执行删除操作");

                if (!await CanAccessProjectAsync(dailyReport.ProjectId, currentUser))
                    return (false, "你没有该项目的日报访问权限");

                bool within24Hours = (AppTime.Now - dailyReport.CreatedAt) <= TimeSpan.FromHours(24);
                if (!within24Hours)
                    return (false, "超过24小时，无法删除项目日报");

                bool isCreator = dailyReport.ReporterId == currentUser.Id;
                bool isProjectAdmin = await _context.ProjectUsers
                   .AnyAsync(pu => pu.ProjectId == dailyReport.ProjectId && pu.UserId == currentUser.Id && pu.ProjectRole == (int)ProjectRole.Admin);
                bool isProjectLeader = dailyReport.Project.LeaderUserId == currentUser.Id;
                bool isSystemAdmin = currentUser.Role == UserRole.systemAdmin;

                if (!(isCreator || isProjectLeader || isProjectAdmin || isSystemAdmin))
                    return (false, "无权限删除此项目日报");

                string reporterName = await GetReporterName(dailyReport.ReporterId);
                var attachments = await GetAttachmentsByReportId(dailyReport.Id);
                var attachmentNames = attachments.Select(a => a.FileName).ToList();

                dailyReport.IsDeleted = true;
                dailyReport.LastModifiedAt = AppTime.Now;
                _context.DailyReport.Update(dailyReport);
                await _context.SaveChangesAsync();

                var deleteLog = new ChangeLog
                {
                    OperationType = OperationType.删除,
                    OperationStatus = OperationStatus.成功,
                    OperatedAt = DateTime.UtcNow,
                    OperationTarget = OperationTarget.日报,
                    OperatedByUserId = currentUser.Id,
                    OperatedByUserName = currentUser.UserName,
                    OperatedByRealName = currentUser.RealName,
                    ProjectId = dailyReport.ProjectId,
                    ProjectName = dailyReport.Project?.Name ?? "未知项目",
                    TargetId = dailyReport.Id,
                    TargetName = dailyReport.ReportTitle,
                    BeforeContent =$"上报人：{reporterName}\n" +
                                   $"标题：{dailyReport.ReportTitle}\n" +
                                  $"报告类型：{GetReportTypeName(dailyReport.ReportType)}\n" +
                                  $"报告日期：{dailyReport.ReportDate:yyyy-MM-dd}\n" +
                                  $"创建时间：{dailyReport.CreatedAt:yyyy-MM-dd HH:mm}\n" +
                                  (attachmentNames.Any() ? $"附件：{string.Join("、", attachmentNames)}" : "无附件") +
                                  "\n状态：正常",
                    AfterContent = $"状态：已删除"
                };
                _context.ChangeLogs.Add(deleteLog);
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();
                return (true, "项目日报已成功删除");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return (false, $"删除失败：{ex.Message}");
            }
        }

        private IQueryable<Project> ApplyProjectAccess(IQueryable<Project> query, ApplicationUser user)
        {
            if (user.Role == UserRole.systemAdmin) return query;
            return query.Where(project => project.LeaderUserId == user.Id
                || _context.ProjectUsers.Any(member => member.ProjectId == project.Id
                    && member.UserId == user.Id
                    && (project.IsEncrypted != '2' || member.ProjectRole == (int)ProjectRole.Admin)));
        }

        private IQueryable<DailyReport> ApplyReportAccess(IQueryable<DailyReport> query, ApplicationUser user)
        {
            if (user.Role == UserRole.systemAdmin) return query;
            // 团队汇报（ReportType=4）：仅项目管理员（ProjectRole.Admin，负责人同时也是管理员）可见
            // 其余报告：按原项目成员权限
            return query.Where(report =>
                (report.ReportType == 4 &&
                    _context.ProjectUsers.Any(pu => pu.ProjectId == report.ProjectId
                        && pu.UserId == user.Id
                        && pu.ProjectRole == (int)ProjectRole.Admin))
                || (report.ReportType != 4
                    && (report.Project!.LeaderUserId == user.Id
                        || _context.ProjectUsers.Any(member => member.ProjectId == report.ProjectId
                            && member.UserId == user.Id
                            && (report.Project.IsEncrypted != '2' || member.ProjectRole == (int)ProjectRole.Admin)))));
        }

        private static string CleanMarkdownContent(string content)
        {
            if (string.IsNullOrEmpty(content)) return "无内容";

            var cleaned = Regex.Replace(content, @"[#*>`\-_\[\]]", "", RegexOptions.Multiline);
            cleaned = Regex.Replace(cleaned, @"\!\[.*?\]\(.*?\)", "（图片）", RegexOptions.Multiline);
            cleaned = Regex.Replace(cleaned, @"\n{2,}", "\n", RegexOptions.Multiline);
            return cleaned.Length > 200 ? cleaned.Substring(0, 200) + "..." : cleaned;
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }

        private static string GetFileIconClass(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLower();
            return ext switch
            {
                ".pdf" => "fa-file-pdf text-danger",
                ".doc" or ".docx" => "fa-file-word text-primary",
                ".xls" or ".xlsx" => "fa-file-excel text-success",
                ".ppt" or ".pptx" => "fa-file-powerpoint text-warning",
                ".jpg" or ".jpeg" or ".png" => "fa-file-image text-info",
                ".txt" => "fa-file-alt text-secondary",
                _ => "fa-file text-dark"
            };
        }

        private string GetReportTypeName(int reportType)
        {
            return reportType switch
            {
                1 => "日报",
                2 => "周报",
                3 => "月报",
                _ => "其他"
            };
        }
    }
}

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Projects;

[Authorize]
public class MeetingPreparationListModel : PageModel
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAntiforgery _antiforgery;

    public MeetingPreparationListModel(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IAntiforgery antiforgery)
    {
        _context = context;
        _userManager = userManager;
        _antiforgery = antiforgery;
    }

    [BindProperty(SupportsGet = true)] public int? FilterProjectId { get; set; }
    public List<DraftListItem> Drafts { get; set; } = new();
    public string AntiforgeryToken { get; set; } = string.Empty;
    public string? LoadError { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        AntiforgeryToken = _antiforgery.GetAndStoreTokens(HttpContext).RequestToken ?? string.Empty;

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Forbid();

        var isSystemAdmin = user.Role == UserRole.systemAdmin;

        // 当前用户参与的所有项目ID（作为负责人或成员）
        List<int> participatoryProjectIds;
        // 仅负责人项目ID（用于删除权限判断）
        List<int> leaderProjectIds;
        if (isSystemAdmin)
        {
            participatoryProjectIds = await _context.Project.AsNoTracking()
                .Where(p => !p.IsDeleted)
                .Select(p => p.Id)
                .ToListAsync();
            leaderProjectIds = participatoryProjectIds;
        }
        else
        {
            var adminLeaderProjectIds = await _context.Project
                .AsNoTracking()
                .Where(p => !p.IsDeleted && p.LeaderUserId == user.Id)
                .Select(p => p.Id)
                .ToListAsync();
            var adminMemberProjectIds = await _context.ProjectUsers
                .AsNoTracking()
                .Where(pu => pu.UserId == user.Id && pu.ProjectRole == (int)ProjectRole.Admin && !pu.Project.IsDeleted)
                .Select(pu => pu.ProjectId)
                .ToListAsync();
            leaderProjectIds = adminLeaderProjectIds.Concat(adminMemberProjectIds).Distinct().ToList();

            var memberProjectIds = _context.ProjectUsers
                .AsNoTracking()
                .Where(pu => pu.UserId == user.Id && !pu.Project.IsDeleted)
                .Select(pu => pu.ProjectId);
            participatoryProjectIds = leaderProjectIds.Concat(await memberProjectIds.ToListAsync()).Distinct().ToList();
        }

        // 草稿可见条件：自己创建的，或者所选项目中有任何一个是自己参与的；系统管理员看全部
        var allDraftsQuery = _context.MeetingPrepDrafts.AsNoTracking().Include(d => d.Creator).Where(d => !d.IsDeleted);
        var allDrafts = isSystemAdmin
            ? await allDraftsQuery.OrderByDescending(d => d.LastModifiedAt).ToListAsync()
            : await allDraftsQuery
                .Where(d => d.CreatorId == user.Id || participatoryProjectIds.Count > 0)
                .OrderByDescending(d => d.LastModifiedAt)
                .ToListAsync();

        int[] participatoryArr = participatoryProjectIds.ToArray();
        var drafts = isSystemAdmin
            ? allDrafts
            : allDrafts.Where(d =>
                d.CreatorId == user.Id
                || SafeDeserializeList(d.SelectedProjectIdsJson).Any(pid => participatoryArr.Contains(pid))
            ).ToList();

        // 额外按 FilterProjectId 过滤：如果传入，只保留包含该项目的草稿
        if (FilterProjectId.HasValue)
        {
            drafts = drafts.Where(d => SafeDeserializeList(d.SelectedProjectIdsJson).Contains(FilterProjectId.Value)).ToList();
        }

        Drafts = drafts.Select(d => new DraftListItem
        {
            Id = d.Id,
            Title = d.Title,
            Status = d.Status,
            StatusText = d.Status == MeetingPrepDraftStatus.Draft ? "草稿" : "已定稿",
            CreatedAt = d.CreatedAt,
            LastModifiedAt = d.LastModifiedAt,
            ProjectCount = SafeDeserializeList(d.SelectedProjectIdsJson).Count,
            TaskCount = SafeDeserializeSnapshot(d.TaskSnapshotJson).Count,
            TaskSnapshots = SafeDeserializeSnapshot(d.TaskSnapshotJson),
            IsCreator = d.CreatorId == user.Id,
            CreatorName = d.Creator?.RealName ?? d.Creator?.UserName ?? string.Empty,
            CanDelete = isSystemAdmin
                || d.CreatorId == user.Id
                || SafeDeserializeList(d.SelectedProjectIdsJson).Any(pid => leaderProjectIds.Contains(pid))
        }).ToList();

        return Page();
    }

    public async Task<JsonResult> OnPostDeleteAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return new JsonResult(new { success = false, message = "未登录" });

        var draft = await _context.MeetingPrepDrafts
            .FirstOrDefaultAsync(d => d.Id == id && !d.IsDeleted);
        if (draft == null)
            return new JsonResult(new { success = false, message = "草稿不存在" });

        if (!await CanDeleteDraftAsync(draft, user))
            return new JsonResult(new { success = false, message = "无权限删除该草稿" });

        draft.IsDeleted = true;
        draft.LastModifiedAt = AppTime.Now;
        await _context.SaveChangesAsync();
        return new JsonResult(new { success = true });
    }

    /// <summary>删除权限：草稿创建者 或 草稿涉及项目的负责人/管理员 或 系统管理员</summary>
    private async Task<bool> CanDeleteDraftAsync(MeetingPrepDraft draft, ApplicationUser user)
    {
        if (user.Role == UserRole.systemAdmin) return true;
        if (draft.CreatorId == user.Id) return true;

        var projIds = SafeDeserializeList(draft.SelectedProjectIdsJson);
        if (projIds.Count == 0) return false;

        // 检查用户是否是草稿涉及任一项目的负责人 或 管理员
        return await _context.Project.AnyAsync(p => projIds.Contains(p.Id) && !p.IsDeleted && (p.LeaderUserId == user.Id
            || _context.ProjectUsers.Any(pu => pu.ProjectId == p.Id && pu.UserId == user.Id && pu.ProjectRole == (int)ProjectRole.Admin)));
    }

    private static List<int> SafeDeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<int>>(json) ?? new(); }
        catch { return new(); }
    }

    private static List<DraftTaskSnapshot> SafeDeserializeSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<DraftTaskSnapshot>>(json) ?? new(); }
        catch { return new(); }
    }

    public class DraftListItem
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public MeetingPrepDraftStatus Status { get; set; }
        public string StatusText { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime LastModifiedAt { get; set; }
        public int ProjectCount { get; set; }
        public int TaskCount { get; set; }
        public List<DraftTaskSnapshot> TaskSnapshots { get; set; } = new();
        public bool IsCreator { get; set; }
        public bool CanDelete { get; set; }
        public string CreatorName { get; set; } = string.Empty;
    }

    public class DraftTaskSnapshot
    {
        public int id { get; set; }
        public string? title { get; set; }
        public string? status { get; set; }
        public string? projectName { get; set; }
        public string? assigneeName { get; set; }
        public string? deadline { get; set; }
        public bool isOverdue { get; set; }
    }
}

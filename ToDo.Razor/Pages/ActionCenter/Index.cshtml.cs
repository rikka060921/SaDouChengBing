using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.ActionCenter;

[Authorize]
public sealed class IndexModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager,
    ApprovalRequestService approvals) : PageModel
{
    public IReadOnlyList<PersonalActionCard> Items { get; set; } = [];
    public int TotalPending { get; set; }
    public int UrgentCount { get; set; }
    public int[] PriorityCounts { get; } = new int[4];
    public string[] PriorityLabels { get; } = ["优先处理", "已逾期", "24 小时内到期", "待处理"];
    public int MyActiveTaskCount { get; set; }
    public IReadOnlyList<ToDoTask> MyWork { get; set; } = [];
    public IReadOnlyList<TeamProjectAttention> TeamAttention { get; set; } = [];
    public int? FailedTaskId { get; set; }
    public int SubmittedVersion { get; set; }
    public int SubmittedProgress { get; set; }
    public string SubmittedNote { get; set; } = string.Empty;
    public const int PageSize = 30;
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)TotalPending / PageSize));

    public async Task<IActionResult> OnGetAsync()
    {
        Response.Headers.CacheControl = "private, no-store";
        var user = await userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        if (user.IsDeleted || user.Status != UserStatus.Active) return Forbid();
        MyActiveTaskCount = await new TaskProgressService(context).MyActiveTasks(user.Id).CountAsync(HttpContext.RequestAborted);
        var workbench = new TeamWorkbenchService(context);
        MyWork = await workbench.MyWorkAsync(user.Id, HttpContext.RequestAborted);
        TeamAttention = await workbench.ManagedAttentionAsync(user.Id, AppTime.Now, HttpContext.RequestAborted);
        var cards = await new PersonalActionService(context, approvals).GetAsync(user, AppTime.Now, HttpContext.RequestAborted);
        TotalPending = cards.Count;
        UrgentCount = cards.Count(c => c.Priority <= 1);
        foreach (var card in cards) PriorityCounts[card.Priority]++;
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);
        Items = cards.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostProgressAsync(int taskId, int expectedVersion, int progress, string? note, bool submitForReview)
    {
        var user = await userManager.GetUserAsync(User);
        if (user == null) return Challenge();
        try
        {
            if (!ModelState.IsValid) throw new InvalidOperationException("请输入有效的任务版本及 0—99 的整数进度。");
            await new TaskProgressService(context).SaveAsync(taskId, user.Id, expectedVersion, progress, note, submitForReview, HttpContext.RequestAborted);
            TempData["WorkbenchMessage"] = submitForReview ? "成果已提交验收，已提醒审核人。" : "进展已保存，日报会复用工作记录，无需重复抄写。";
            return RedirectToPage();
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (DbUpdateConcurrencyException) { ModelState.AddModelError(string.Empty, "任务已变化，请复制保留的说明并刷新后重试。"); }
        catch (InvalidOperationException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HttpContext.RequestServices.GetService<ILogger<IndexModel>>()?.LogError(ex, "Workbench progress save failed for task {TaskId}", taskId);
            ModelState.AddModelError(string.Empty, "进展未能保存，你的说明仍保留，请复制备份后重试。");
            FailedTaskId = taskId; SubmittedVersion = expectedVersion; SubmittedProgress = progress; SubmittedNote = note ?? string.Empty;
            return Page();
        }
        context.ChangeTracker.Clear();
        FailedTaskId = taskId; SubmittedVersion = expectedVersion; SubmittedProgress = progress; SubmittedNote = note ?? string.Empty;
        return await OnGetAsync();
    }
}

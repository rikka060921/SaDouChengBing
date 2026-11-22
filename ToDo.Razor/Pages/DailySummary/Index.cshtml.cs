using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;
using ToDo.Entities.DailySummary;

namespace ToDo.Razor.Pages.DailySummary;

[Authorize]
public class IndexModel(ApplicationDbContext context, ILogger<IndexModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public DateTime SelectDate { get; set; } = AppTime.Today;
    [BindProperty(SupportsGet = true)] public int? UserId { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? FilterStartDate { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? FilterEndDate { get; set; }
    public DailyWorkSummary? Summary { get; set; }
    public List<DailyProjectSummaryDetail> ProjectDetailList { get; set; } = [];
    public List<DailyWorkSummary> SummaryHistoryList { get; set; } = [];
    public List<DailyReportSupplement> ManualEntries { get; set; } = [];
    public List<DailyReport> ProjectDiaryReports { get; set; } = [];
    public HashSet<DateTime> ManualDates { get; set; } = [];
    public string ErrorMessage { get; set; } = string.Empty;
    public string ViewingUserName { get; set; } = "我的";
    public bool CanGenerateForCurrentUser { get; set; } = true;
    public bool IsAnyProjectLeader { get; set; }

    public async Task OnGetAsync()
    {
        Response.Headers.CacheControl = "private, no-store";
        try
        {
            if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
            { ErrorMessage = "请先登录后查看数据"; return; }
            var actor = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == actorId && !u.IsDeleted);
            if (actor == null) { ErrorMessage = "用户信息不存在"; return; }
            var targetId = actor.Role == UserRole.systemAdmin && UserId.HasValue ? UserId.Value : actorId;
            var target = targetId == actorId ? actor : await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == targetId && !u.IsDeleted);
            if (target == null) { ErrorMessage = "要查看的用户不存在"; return; }
            CanGenerateForCurrentUser = actorId == targetId;
            UserId = CanGenerateForCurrentUser ? null : targetId;
            ViewingUserName = CanGenerateForCurrentUser ? "我的" : $"{target.RealName ?? target.UserName}的";
            IsAnyProjectLeader = await MemberReportAccess.Projects(context, actor).AnyAsync();
            var projects = context.Project.Where(p => !p.IsDeleted && (actor.Role == UserRole.systemAdmin
                || p.LeaderUserId == actorId || context.ProjectUsers.Any(m => m.ProjectId == p.Id && m.UserId == actorId))).Select(p => p.Id);
            Summary = await context.DailyWorkSummaries.AsNoTracking().Where(s => s.UserId == targetId
                && s.SummaryDate == SelectDate.Date && !s.IsDeleted).OrderByDescending(s => s.CreateTime).FirstOrDefaultAsync();
            if (Summary != null)
                ProjectDetailList = await context.DailyProjectSummaryDetails.AsNoTracking()
                    .Where(d => d.DailySummaryId == Summary.Id && !d.IsDeleted && projects.Contains(d.ProjectId))
                    .OrderBy(d => d.ProjectId).ToListAsync();
            var service = new WorkReportService(context, null!);
            ManualEntries = await service.PersonalNotesAsync(actorId, targetId, SelectDate, HttpContext.RequestAborted);
            var notes = await service.PersonalNotesAsync(actorId, targetId, null, HttpContext.RequestAborted, FilterStartDate, FilterEndDate);
            var projectReports = context.DailyReport.AsNoTracking().Include(r => r.Project).Where(r => r.ReporterId == targetId
                && r.ReportType == 1 && !r.IsDeleted && projects.Contains(r.ProjectId));
            var day = SelectDate.Date; var nextDay = day.AddDays(1);
            ProjectDiaryReports = await projectReports.Where(r => r.ReportDate >= day && r.ReportDate < nextDay)
                .OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync();
            ManualDates = notes.Select(n => n.ReportDate).ToHashSet();
            // Always load history, including when the selected day has no generated summary.
            var history = context.DailyWorkSummaries.AsNoTracking().Where(s => s.UserId == targetId && !s.IsDeleted);
            if (FilterStartDate.HasValue) history = history.Where(s => s.SummaryDate >= FilterStartDate.Value.Date);
            if (FilterEndDate.HasValue) history = history.Where(s => s.SummaryDate <= FilterEndDate.Value.Date);
            SummaryHistoryList = await history.OrderByDescending(s => s.SummaryDate).ThenByDescending(s => s.CreateTime).Take(100).ToListAsync();
            var legacyHistory = projectReports;
            if (FilterStartDate.HasValue) legacyHistory = legacyHistory.Where(r => r.ReportDate >= FilterStartDate.Value.Date);
            if (FilterEndDate.HasValue) { var until = FilterEndDate.Value.Date.AddDays(1); legacyHistory = legacyHistory.Where(r => r.ReportDate < until); }
            var reportHistory = await legacyHistory.OrderByDescending(r => r.ReportDate).ThenByDescending(r => r.Id).Take(100).ToListAsync();
            var knownDates = SummaryHistoryList.Select(s => s.SummaryDate.Date).ToHashSet();
            foreach (var group in notes.Where(n => (!FilterStartDate.HasValue || n.ReportDate >= FilterStartDate.Value.Date)
                && (!FilterEndDate.HasValue || n.ReportDate <= FilterEndDate.Value.Date)).GroupBy(n => n.ReportDate))
            {
                if (knownDates.Add(group.Key)) SummaryHistoryList.Add(new DailyWorkSummary { UserId = targetId,
                    SummaryDate = group.Key, Title = $"{group.Key:yyyy-MM-dd} 人工日报", SummaryStatus = 1,
                    CreateTime = group.Max(n => n.CreatedAt), TotalSummary = string.Join("\n", group.Select(n => $"{n.Project.Name}：{n.Content}")) });
            }
            foreach (var group in reportHistory.GroupBy(r => r.ReportDate.Date))
                if (knownDates.Add(group.Key)) SummaryHistoryList.Add(new DailyWorkSummary { Id = -group.First().Id, UserId = targetId,
                    SummaryDate = group.Key, Title = $"{group.Key:yyyy-MM-dd} 已提交项目日报", SummaryStatus = 1,
                    CreateTime = group.Max(r => r.CreatedAt), TotalSummary = string.Join("\n", group.Select(r => $"{r.Project.Name}：{r.ReportContent}")) });
            SummaryHistoryList = SummaryHistoryList.OrderByDescending(s => s.SummaryDate).ThenByDescending(s => s.CreateTime).Take(100).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading personal reports failed for {Date}", SelectDate);
            ErrorMessage = "日报加载失败，请稍后重试";
        }
    }
}

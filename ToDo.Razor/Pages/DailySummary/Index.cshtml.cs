using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;
using ToDo.Entities.DailySummary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace ToDo.Razor.Pages.DailySummary
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(ApplicationDbContext context, ILogger<IndexModel> logger)
        {
            _context = context;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public DateTime SelectDate { get; set; } = AppTime.Now.Date;

        [BindProperty(SupportsGet = true)]
        public int? UserId { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? FilterStartDate { get; set; }

        [BindProperty(SupportsGet = true)]
        public DateTime? FilterEndDate { get; set; }

        public DailyWorkSummary? Summary { get; set; }
        public List<DailyProjectSummaryDetail> ProjectDetailList { get; set; } = new();
        public List<DailyWorkSummary> SummaryHistoryList { get; set; } = new();
        public string ErrorMessage { get; set; } = string.Empty;
        public string ViewingUserName { get; set; } = "我的";
        public bool CanGenerateForCurrentUser { get; set; } = true;
        /// <summary>当前用户是否是任一项目的管理员（用于显示「团队日报」Tab）</summary>
        public bool IsAnyProjectLeader { get; set; }

        public async Task OnGetAsync()
        {
            try
            {

                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId) || !int.TryParse(userId, out int currentUserId))
                {
                    ErrorMessage = "请先登录后查看数据";
                    ProjectDetailList = new List<DailyProjectSummaryDetail>();
                    return;
                }

                // 获取当前用户完整信息（匹配会议模块的currentUser对象）
                var currentUser = await _context.Users
                    .FirstOrDefaultAsync(u => u.Id == currentUserId);

                if (currentUser == null)
                {
                    ErrorMessage = "用户信息不存在";
                    ProjectDetailList = new List<DailyProjectSummaryDetail>();
                    return;
                }

                var targetUserId = currentUser.Role == UserRole.systemAdmin && UserId.HasValue
                    ? UserId.Value
                    : currentUserId;
                CanGenerateForCurrentUser = targetUserId == currentUserId;
                UserId = CanGenerateForCurrentUser ? null : targetUserId;
                var targetUser = targetUserId == currentUserId
                    ? currentUser
                    : await _context.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == targetUserId && !item.IsDeleted);
                if (targetUser == null)
                {
                    ErrorMessage = "要查看的用户不存在";
                    return;
                }
                ViewingUserName = targetUserId == currentUserId
                    ? "我的"
                    : $"{(string.IsNullOrWhiteSpace(targetUser.RealName) ? targetUser.UserName : targetUser.RealName)}的";

                // 判断是否是任一项目的管理员（系统管理员也算）
                if (currentUser.Role == UserRole.systemAdmin)
                {
                    IsAnyProjectLeader = await _context.Project.AsNoTracking()
                        .AnyAsync(p => !p.IsDeleted && p.Status == ProjectStatus.Active);
                }
                else
                {
                    IsAnyProjectLeader = await _context.ProjectUsers.AsNoTracking()
                        .AnyAsync(pu => pu.UserId == currentUser.Id && pu.ProjectRole == (int)ProjectRole.Admin
                            && !pu.Project.IsDeleted && pu.Project.Status == ProjectStatus.Active);
                }

                // 查询当日汇总主数据
                Summary = await _context.DailyWorkSummaries
                    .FirstOrDefaultAsync(s => s.UserId == targetUserId
                        && s.SummaryDate.Date == SelectDate.Date
                        && !s.IsDeleted);

                if (Summary == null)
                {
                    ProjectDetailList = new List<DailyProjectSummaryDetail>();
                    return;
                }

                IQueryable<int> accessibleProjectIds;
                // 1. 系统管理员：查看所有项目（复用会议模块systemAdmin判断）
                bool isSystemAdmin = currentUser.Role == UserRole.systemAdmin;

                if (isSystemAdmin)
                {
                    // 系统管理员：全量未删除项目
                    accessibleProjectIds = _context.Project
                        .Where(p => !p.IsDeleted)
                        .Select(p => p.Id);
                }
                else
                {
                    // 2. 普通用户：项目负责人或项目成员。
                    accessibleProjectIds = _context.Project
                        .Where(project => !project.IsDeleted
                            && (project.LeaderUserId == currentUser.Id
                                || _context.ProjectUsers.Any(member => member.ProjectId == project.Id && member.UserId == currentUser.Id)))
                        .Select(project => project.Id);
                }

                // 筛选有权限的汇总明细
                ProjectDetailList = await _context.DailyProjectSummaryDetails
                    .Where(d => d.DailySummaryId == Summary.Id
                          && accessibleProjectIds.Contains(d.ProjectId))
                    .OrderByDescending(d => d.CreateTime)
                    .ToListAsync();

                // 无权限提示（和系统提示风格统一）
                if (!ProjectDetailList.Any() && !isSystemAdmin)
                {
                    ErrorMessage = "无权限查看此汇总（仅项目成员/项目管理员/系统管理员可查看）";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load daily summary for date {SelectDate}", SelectDate);
                ErrorMessage = "数据加载失败，请稍后重试";
                ProjectDetailList = new List<DailyProjectSummaryDetail>();
            }

            // 加载个人日报历史列表（按日期倒序，支持日期范围筛选）
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!string.IsNullOrEmpty(userId) && int.TryParse(userId, out int currentUserId))
                {
                    var currentUser = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == currentUserId);
                    if (currentUser != null)
                    {
                        var targetUserId = currentUser.Role == UserRole.systemAdmin && UserId.HasValue
                            ? UserId.Value
                            : currentUserId;

                        var historyQuery = _context.DailyWorkSummaries.AsNoTracking()
                            .Where(s => s.UserId == targetUserId && !s.IsDeleted);

                        if (FilterStartDate.HasValue)
                            historyQuery = historyQuery.Where(s => s.SummaryDate.Date >= FilterStartDate.Value.Date);
                        if (FilterEndDate.HasValue)
                            historyQuery = historyQuery.Where(s => s.SummaryDate.Date <= FilterEndDate.Value.Date);

                        SummaryHistoryList = await historyQuery
                            .OrderByDescending(s => s.SummaryDate)
                            .ThenByDescending(s => s.CreateTime)
                            .Take(100)
                            .ToListAsync();
                    }
                }
            }
            catch
            {
                // 列表加载失败不影响主页面，忽略错误
                SummaryHistoryList = new List<DailyWorkSummary>();
            }

        }
    }
    }

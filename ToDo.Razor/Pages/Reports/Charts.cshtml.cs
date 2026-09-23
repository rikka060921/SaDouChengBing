using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Razor.Pages
{
    public class ProjectStatsModel : PageModel
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        // 活跃项目列表（带名称和成员数）
        public List<dynamic> ActiveProjects { get; set; } = new();

        // 成员参与项目数（带真实姓名）- 仅计算活跃项目
        public List<dynamic> UserParticipations { get; set; } = new();

        // 项目状态数据（活跃/归档数量）
        public dynamic ProjectStatusData { get; set; } = new { ActiveCount = 0, ArchivedCount = 0, TotalCount = 0 };

        // 项目创建趋势数据（按月份统计）
        public List<dynamic> ProjectCreationTrend { get; set; } = new();

        public ProjectStatsModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Challenge();

            var projectQuery = _context.Project.AsNoTracking().Where(project => !project.IsDeleted);
            if (user.Role != UserRole.systemAdmin)
            {
                projectQuery = projectQuery.Where(project => project.LeaderUserId == user.Id
                    || _context.ProjectUsers.Any(member => member.ProjectId == project.Id && member.UserId == user.Id));
            }
            var accessibleProjectIds = await projectQuery.Select(project => project.Id).ToListAsync();

            // 1. 活跃项目成员数量数据
            ActiveProjects = await _context.Project
               .Where(p => accessibleProjectIds.Contains(p.Id) && p.Status == ProjectStatus.Active && !p.IsDeleted)
               .Select(p => new
               {
                   ProjectName = p.Name,
                   MemberCount = p.ProjectUsers.Count
               })
               .OrderByDescending(p => p.MemberCount)
               .ToListAsync<dynamic>();

            // 2. 成员参与项目数数据
            UserParticipations = await _context.ProjectUsers
               .Where(pu => accessibleProjectIds.Contains(pu.ProjectId)
                   && pu.Project.Status == ProjectStatus.Active && !pu.Project.IsDeleted)
               .GroupBy(pu => new { pu.UserId, pu.User.RealName })
               .Select(g => new
               {
                   UserName = g.Key.RealName,
                   ProjectCount = g.Count()
               })
               .OrderByDescending(g => g.ProjectCount)
               .Take(10)
               .ToListAsync<dynamic>();

            // 3. 项目状态占比数据
            var activeCount = await _context.Project
               .CountAsync(p => accessibleProjectIds.Contains(p.Id) && p.Status == ProjectStatus.Active && !p.IsDeleted);
            var archivedCount = await _context.Project
               .CountAsync(p => accessibleProjectIds.Contains(p.Id) && p.Status == ProjectStatus.Archived && !p.IsDeleted);

            ProjectStatusData = new
            {
                ActiveCount = activeCount,
                ArchivedCount = archivedCount,
                TotalCount = activeCount + archivedCount
            };

            // 4. 项目创建时间趋势数据
            var twelveMonthsAgo = AppTime.ToUtc(AppTime.Now.AddMonths(-12));
            var rawProjects = await _context.Project
               .Where(p => accessibleProjectIds.Contains(p.Id) && p.CreatedAt >= twelveMonthsAgo && !p.IsDeleted)
               .ToListAsync();

            ProjectCreationTrend = rawProjects
               .Select(p => new
               {
                   Project = p,
                   LocalCreatedAt = AppTime.ToBeijingTime(p.CreatedAt)
               })
               .GroupBy(p => new
               {
                   Month = p.LocalCreatedAt.ToString("yyyy-MM"),
                   SortDate = new DateTime(p.LocalCreatedAt.Year, p.LocalCreatedAt.Month, 1)
               })
               .OrderBy(g => g.Key.SortDate)
               .Select(g => new
               {
                   Month = g.Key.Month,
                   // 该月份创建的活跃项目数（Status为Active）
                   ActiveCount = g.Count(p => p.Project.Status == ProjectStatus.Active),
                   // 该月份创建的归档项目数（Status为Archived）
                   ArchivedCount = g.Count(p => p.Project.Status == ProjectStatus.Archived)
               })
               .ToList<dynamic>();

            return Page();
        }
    }
}

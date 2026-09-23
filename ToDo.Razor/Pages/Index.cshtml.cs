using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Razor.Pages
{
    public class IndexModel : PageModel
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public IndexModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        public IList<Project> Projects { get; set; } = new List<Project>();

        public async Task OnGetAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return;

            var query = _context.Project.AsNoTracking().Where(project => !project.IsDeleted);
            if (user.Role != UserRole.systemAdmin)
            {
                query = query.Where(project => project.LeaderUserId == user.Id
                    || _context.ProjectUsers.Any(member => member.ProjectId == project.Id && member.UserId == user.Id));
            }
            Projects = await query.OrderByDescending(project => project.CreatedAt).ToListAsync();
        }
    }
}

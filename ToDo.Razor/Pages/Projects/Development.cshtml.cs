using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Projects;

[Authorize]
public sealed class DevelopmentModel(ApplicationDbContext context,ProjectDocumentService documents,UserManager<ApplicationUser> users,ILogger<DevelopmentModel> logger):PageModel
{
    [BindProperty(SupportsGet=true)]public int ProjectId{get;set;}
    [BindProperty(SupportsGet=true)]public string? Author{get;set;}
    [BindProperty(SupportsGet=true)]public string? Search{get;set;}
    [BindProperty(SupportsGet=true)]public int PageNumber{get;set;}=1;
    public string ProjectName{get;private set;}="";
    public string? Error{get;private set;}
    public ProjectDevelopmentLedger? Ledger{get;private set;}
    public ProjectDocument? SourceDocument{get;private set;}
    public List<ProjectGitChange> Changes{get;private set;}=[];
    public List<(ProjectHistoryParticipant Person,string Account)> People{get;}=[];
    public List<ChangeLog> Imports{get;private set;}=[];
    public int Total{get;private set;}
    public int TotalPages=>Math.Max(1,(Total+29)/30);
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl="private, no-store";
        var actor=await users.GetUserAsync(User);if(actor==null)return Challenge();
        try
        {
            var data=await new ProjectDevelopmentLedgerReader(context,documents).ReadAsync(ProjectId,actor.Id,cancellationToken);
            ProjectName=await context.Project.Where(p=>p.Id==ProjectId&&!p.IsDeleted).Select(p=>p.Name).SingleAsync(cancellationToken);
            if(data==null)return Page();
            (Ledger,SourceDocument)=data.Value;
            var query=Ledger.Changes.AsEnumerable();
            if(!string.IsNullOrWhiteSpace(Author))query=query.Where(c=>c.Author==Author);
            Search=Search?.Trim();
            if(!string.IsNullOrEmpty(Search))query=query.Where(c=>c.Subject.Contains(Search,StringComparison.OrdinalIgnoreCase)||c.Commit.Contains(Search,StringComparison.OrdinalIgnoreCase)||c.Message.Contains(Search,StringComparison.OrdinalIgnoreCase));
            var matching=query.OrderByDescending(c=>c.CommittedAt).ThenBy(c=>c.Commit).ToList();
            Total=matching.Count;PageNumber=Math.Clamp(PageNumber,1,TotalPages);Changes=matching.Skip((PageNumber-1)*30).Take(30).ToList();
            foreach(var person in Ledger.Participants)
            {
                var account=await context.Users.AsNoTracking().SingleOrDefaultAsync(u=>u.UserName==person.UserName&&!u.IsDeleted,cancellationToken);
                var linked=account!=null&&await context.ProjectUsers.AnyAsync(m=>m.ProjectId==ProjectId&&m.UserId==account.Id,cancellationToken);
                People.Add((person,account==null?"账号未匹配，不自动开通":account.Status!=UserStatus.Active?"账号停用，未开通访问":linked?"已关联项目成员":"账号存在，尚未关联项目"));
            }
            Imports=await context.ChangeLogs.AsNoTracking().Where(c=>c.ProjectId==ProjectId&&c.AfterContent!=null&&c.AfterContent.StartsWith("[历史补录]"))
                .OrderByDescending(c=>c.OperatedAt).Take(5).ToListAsync(cancellationToken);
            return Page();
        }
        catch(UnauthorizedAccessException){return Forbid();}
        catch(Exception ex)when(ex is ArgumentException or IOException)
        {logger.LogWarning(ex,"Cannot read development ledger for project {ProjectId}",ProjectId);Error="开发记录原件读取或校验失败，请检查资料版本与运行文件。";return Page();}
    }
}

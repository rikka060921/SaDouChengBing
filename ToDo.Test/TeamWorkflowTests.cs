using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using ToDo.Domain;
using ToDo.Entities;
using ToDo.Razor.Pages.Account;
using TaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Test;

public sealed class TeamWorkflowTests
{
    [Theory]
    [InlineData(null, "/ActionCenter/Index")]
    [InlineData("/", "/ActionCenter/Index")]
    [InlineData("~/", "/ActionCenter/Index")]
    [InlineData("/Index", "/ActionCenter/Index")]
    [InlineData("https://example.invalid/", "/ActionCenter/Index")]
    [InlineData("//example.invalid/", "/ActionCenter/Index")]
    [InlineData("/\\example.invalid/", "/ActionCenter/Index")]
    [InlineData("/Tasks/Details/3?tab=delivery#result", "/Tasks/Details/3?tab=delivery#result")]
    [InlineData("~/Projects/Details?id=10", "~/Projects/Details?id=10")]
    public void LoginReturnUrl_OnlyAcceptsLocalLinks(string? input,string expected)
    {
        var helper = new UrlHelper(new ActionContext(new DefaultHttpContext(),new RouteData(),new ActionDescriptor()));
        Assert.Equal(expected,LoginModel.SafeReturnUrl(helper,input));
    }

    [Fact]
    public void RootHasOneCanonicalWorkbenchDestination()
    {
        var result = Assert.IsType<RedirectToPageResult>(new ToDo.Razor.Pages.IndexModel().OnGet());
        Assert.Equal("/ActionCenter/Index",result.PageName);
    }

    [Theory]
    [InlineData(AgentTaskExecutionStatus.Pending,"等待")]
    [InlineData(AgentTaskExecutionStatus.WaitingApproval,"尚未执行")]
    [InlineData(AgentTaskExecutionStatus.AwaitingPlanConfirmation,"尚未正式执行")]
    [InlineData(AgentTaskExecutionStatus.AwaitingConfirmation,"等待验收")]
    [InlineData(AgentTaskExecutionStatus.Failed,"未完成")]
    public void AgentHintsDoNotMisrepresentPlanningApprovalOrFailureAsCompletion(AgentTaskExecutionStatus state,string expected)
        => Assert.Contains(expected,AgentWorkPresentation.NextStep(state));

    [Fact]
    public void MeetingOverviewCountsPendingCreatesUpdatesAndConfirmedSeparately()
    {
        var result=MeetingCloseoutSummary.From([
            new(){SyncStatus="待确认创建",AssigneeId=2,Deadline=AppTime.Now.AddDays(1)},
            new(){SyncStatus="待确认创建"},
            new(){SyncStatus="待确认更新",MatchedTaskId=8},
            new(){SyncStatus="已创建",IsConfirmed=true},
            new(){SyncStatus="待处理"}]);
        Assert.Equal(new MeetingCloseoutSummary(2,1,1,1),result);
    }

    [Fact]
    public async Task Workbench_OrdersOnlyMyActionableWork_AndDoesNotExposeManagementDataToMembers()
    {
        await using var f=await TaskProgressTests.Fixture.CreateAsync();
        f.Task.EndTime=AppTime.Now.AddDays(2);
        var first=new ToDoTask{Title="同期限高优先级",ProjectId=f.Project.Id,CreatorId=f.Leader.Id,AssigneeId=f.Owner.Id,EndTime=AppTime.Now.Date,Priority=TaskPriority.High};
        var second=new ToDoTask{Title="同期限低优先级",ProjectId=f.Project.Id,CreatorId=f.Leader.Id,AssigneeId=f.Owner.Id,EndTime=AppTime.Now.Date,Priority=TaskPriority.Low};
        var withoutDeadline=new ToDoTask{Title="未设期限",ProjectId=f.Project.Id,CreatorId=f.Leader.Id,AssigneeId=f.Owner.Id,Priority=TaskPriority.High};
        f.Db.AddRange(first,second,withoutDeadline);await f.Db.SaveChangesAsync();
        var workbench=new TeamWorkbenchService(f.Db);
        Assert.Equal(new[]{first.Id,second.Id,f.Task.Id,withoutDeadline.Id},(await workbench.MyWorkAsync(f.Owner.Id)).Select(t=>t.Id));
        Assert.Empty(await workbench.MyWorkAsync(f.Other.Id));
        Assert.Empty(await workbench.ManagedAttentionAsync(f.Owner.Id,AppTime.Now));
        Assert.Null(await workbench.GettingStartedAsync(f.Project.Id,f.Owner.Id));
        var attention=Assert.Single(await workbench.ManagedAttentionAsync(f.Leader.Id,AppTime.Now));Assert.Equal(2,attention.Overdue);
        f.Project.Status=ProjectStatus.Archived;await f.Db.SaveChangesAsync();
        Assert.Empty(await workbench.MyWorkAsync(f.Owner.Id));Assert.Empty(await workbench.ManagedAttentionAsync(f.Leader.Id,AppTime.Now));
        Assert.Null(await workbench.GettingStartedAsync(f.Project.Id,f.Leader.Id));
    }

    [Fact]
    public async Task GettingStarted_IsReadOnlyAndDoesNotGrantAIDataAccess()
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();
        var workbench=new TeamWorkbenchService(f.Context);
        var start=await workbench.GettingStartedAsync(10,1);
        Assert.NotNull(start);Assert.False(start.HasMembers);Assert.False(start.HasDocuments);Assert.False(start.HasTask);Assert.False(start.HasAuthorizedAgent);
        Assert.Null(await workbench.GettingStartedAsync(10,2));
        Assert.Empty(await f.Context.AgentDocumentPermissions.ToListAsync());
        await f.Upload("背景.md",Encoding.UTF8.GetBytes("项目背景"));await f.Grant();
        var prepared=await workbench.GettingStartedAsync(10,1);Assert.True(prepared!.HasDocuments);Assert.True(prepared.HasAuthorizedAgent);
        f.Agent.IsEnabled=false;await f.Context.SaveChangesAsync();
        Assert.False((await workbench.GettingStartedAsync(10,1))!.HasAuthorizedAgent);
    }

    [Fact]
    public async Task WorkbenchPost_UsesExistingAtomicProgressFlow_AndPreservesInputOnVersionConflict()
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();
        f.Context.ProjectUsers.Add(new(){ProjectId=10,UserId=2,ProjectRole=(int)ProjectRole.Member});
        var task=new ToDoTask{Title="新人试用反馈",ProjectId=10,CreatorId=1,ReviewerId=1,AssigneeId=2};f.Context.Add(task);await f.Context.SaveChangesAsync();
        var page=f.Workbench(2);var version=task.ConcurrencyVersion;
        Assert.IsType<RedirectToPageResult>(await page.OnPostProgressAsync(task.Id,version,30,"已整理三个问题",false));
        Assert.Empty(await f.Context.UserNotifications.ToListAsync());
        page=f.Workbench(2);
        Assert.IsType<PageResult>(await page.OnPostProgressAsync(task.Id,version,50,"不要丢失的说明",false));
        Assert.False(page.ModelState.IsValid);Assert.Equal("不要丢失的说明",page.SubmittedNote);Assert.Equal(30,(await f.Context.ToDoTasks.AsNoTracking().SingleAsync()).Progress);
        page=f.Workbench(2);version=(await f.Context.ToDoTasks.AsNoTracking().SingleAsync()).ConcurrencyVersion;
        Assert.IsType<RedirectToPageResult>(await page.OnPostProgressAsync(task.Id,version,50,"反馈清单已上传，请验收",true));
        Assert.Equal(TaskStatus.PendingConfirmation,(await f.Context.ToDoTasks.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(1,Assert.Single(await f.Context.UserNotifications.ToListAsync()).UserId);
    }

    [Fact]
    public async Task TextEditing_CreatesVersion_PreservesSource_RejectsStaleAndOutsider()
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();
        var source=await f.Upload("使用说明.md",Encoding.UTF8.GetBytes("# 原文"));
        var service=new ProjectDocumentEditingService(f.Context,f.Documents);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ReadAsync(10,source.Id,0,2));
        f.Context.ProjectUsers.Add(new(){ProjectId=10,UserId=2,ProjectRole=(int)ProjectRole.Member});await f.Context.SaveChangesAsync();
        var snapshot=await service.ReadAsync(10,source.Id,0,2);
        await service.SaveAsync(10,source.Id,0,2,snapshot.Token,"# 补充验收步骤");
        Assert.Equal("# 原文",await File.ReadAllTextAsync(f.Documents.ResolveStoragePath(source.StoragePath)));
        var current=await f.Context.ProjectDocuments.AsNoTracking().SingleAsync(d=>d.IsCurrent);Assert.Equal(2,current.VersionNumber);
        Assert.Equal("# 补充验收步骤",await File.ReadAllTextAsync(f.Documents.ResolveStoragePath(current.StoragePath)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.SaveAsync(10,source.Id,0,2,snapshot.Token,"过期覆盖"));
        Assert.Single(await f.Context.ChangeLogs.ToListAsync());
        (await f.Context.Project.FindAsync(10))!.Status=ProjectStatus.Archived;await f.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReadAsync(10,current.Id,0,2));
    }

    [Fact]
    public async Task DraftEditing_IsManagerOnly_RemainsPending_DetectsStaleInput_AndPublishesEditedVersion()
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();var source=await f.Upload("需求.md",Encoding.UTF8.GetBytes("原件"));await f.Grant();
        var materialPage=f.Page();await materialPage.OnPostAiReviseAsync(source.Id,"补充标准");
        var draft=await f.Context.AiDocumentDrafts.SingleAsync();var originalPath=draft.DraftFilePath;
        f.Context.ProjectUsers.Add(new(){ProjectId=10,UserId=2,ProjectRole=(int)ProjectRole.Member});await f.Context.SaveChangesAsync();
        var service=new ProjectDocumentEditingService(f.Context,f.Documents);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ReadAsync(10,0,draft.Id,2));
        var snapshot=await service.ReadAsync(10,0,draft.Id,1);
        await service.SaveAsync(10,0,draft.Id,1,snapshot.Token,"# 人工核对后的完整标准");
        Assert.Equal(DraftAuditStatus.Pending,draft.AuditStatus);Assert.Equal("# 人工核对后的完整标准",draft.RevisedContent);
        Assert.True(File.Exists(f.Documents.ResolveStoragePath(originalPath)));Assert.Single(await f.Context.ProjectDocuments.ToListAsync());
        Assert.Contains(originalPath,(await f.Context.ChangeLogs.SingleAsync()).BeforeContent);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.SaveAsync(10,0,draft.Id,1,snapshot.Token,"旧页面覆盖"));
        await materialPage.OnPostAuditDraftAsync(draft.Id,"pass");
        Assert.Equal(DraftAuditStatus.Pass,(await f.Context.AiDocumentDrafts.AsNoTracking().SingleAsync()).AuditStatus);
        var current=await f.Context.ProjectDocuments.AsNoTracking().SingleAsync(d=>d.IsCurrent);
        Assert.Equal("# 人工核对后的完整标准",await File.ReadAllTextAsync(f.Documents.ResolveStoragePath(current.StoragePath)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReadAsync(10,0,draft.Id,1));
        File.Delete(f.Documents.ResolveStoragePath(originalPath));
    }

    [Fact]
    public async Task EditorPage_PreservesSubmittedTextOnConflict_DoesNotPublish()
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();var doc=await f.Upload("待校订.txt",Encoding.UTF8.GetBytes("原文"));
        var page=f.Editor();page.DocumentId=doc.Id;page.ExpectedToken="stale";page.EditedContent="我未保存的文字";
        Assert.IsType<PageResult>(await page.OnPostAsync());
        Assert.False(page.ModelState.IsValid);Assert.Equal("我未保存的文字",page.EditedContent);Assert.Equal("stale",page.ExpectedToken);
        Assert.Single(await f.Context.ProjectDocuments.ToListAsync());
    }

    [Theory]
    [InlineData("pass",true)]
    [InlineData("reject",true)]
    [InlineData("pass",false)]
    public async Task AuditCannotPublishOrRejectConcurrentlyEditedDraft(string action,bool trackedStale)
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();var source=await f.Upload("需求.md",Encoding.UTF8.GetBytes("原文"));await f.Grant();
        var page=f.Page();await page.OnPostAiReviseAsync(source.Id,"补充标准");
        var draft=await f.Context.AiDocumentDrafts.SingleAsync();var token=ProjectDocumentEditingService.Token(draft.RevisedContent??"");
        await f.Context.AiDocumentDrafts.Where(d=>d.Id==draft.Id).ExecuteUpdateAsync(s=>s.SetProperty(d=>d.RevisedContent,"其他人刚校订的新内容"));
        if(!trackedStale) f.Context.ChangeTracker.Clear();
        await page.OnPostAuditDraftAsync(draft.Id,action,token);
        Assert.Contains("校订",page.TempData["ErrorMessage"]?.ToString());
        Assert.Equal(DraftAuditStatus.Pending,(await f.Context.AiDocumentDrafts.AsNoTracking().SingleAsync()).AuditStatus);
        Assert.Equal("其他人刚校订的新内容",(await f.Context.AiDocumentDrafts.AsNoTracking().SingleAsync()).RevisedContent);
        Assert.Single(await f.Context.ProjectDocuments.ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemovedOrDisabledAccountCannotEditOrApprove(bool deleted)
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();var source=await f.Upload("需求.md",Encoding.UTF8.GetBytes("原文"));await f.Grant();
        var page=f.Page();await page.OnPostAiReviseAsync(source.Id,"补充标准");var draft=await f.Context.AiDocumentDrafts.SingleAsync();
        var actor=(await f.Context.Users.FindAsync(1))!;if(deleted)actor.IsDeleted=true;else actor.Status=UserStatus.Inactive;await f.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>new ProjectDocumentEditingService(f.Context,f.Documents).ReadAsync(10,source.Id,0,1));
        Assert.IsType<ForbidResult>(await page.OnPostAuditDraftAsync(draft.Id,"pass"));
        Assert.Equal(DraftAuditStatus.Pending,(await f.Context.AiDocumentDrafts.AsNoTracking().SingleAsync()).AuditStatus);
        Assert.Single(await f.Context.ProjectDocuments.ToListAsync());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("large")]
    [InlineData("encoding")]
    [InlineData("binary")]
    [InlineData("cross-project")]
    public async Task InvalidTextEditsFailWithoutOverwritingData(string reason)
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();
        var bytes=reason=="encoding"?new byte[]{0xff,0xfe,0x00,0xd8}:Encoding.UTF8.GetBytes("原文");
        var source=await f.Upload(reason=="binary"?"原件.pdf":"规范.md",bytes);
        var service=new ProjectDocumentEditingService(f.Context,f.Documents);
        if(reason is "binary" or "encoding" or "cross-project")
            await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ReadAsync(reason=="cross-project"?11:10,source.Id,0,1));
        else await Assert.ThrowsAsync<InvalidOperationException>(()=>service.SaveAsync(10,source.Id,0,1,ProjectDocumentEditingService.Token("原文"),reason=="empty"?" ":new string('字',100001)));
        Assert.Single(await f.Context.ProjectDocuments.ToListAsync());
        Assert.Equal(bytes,await File.ReadAllBytesAsync(f.Documents.ResolveStoragePath(source.StoragePath)));
    }
}

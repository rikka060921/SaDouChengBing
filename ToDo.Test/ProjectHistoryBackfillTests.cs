using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Test;

public sealed class ProjectHistoryBackfillTests
{
    private static string Json(object data)=>JsonSerializer.Serialize(data,ProjectDevelopmentLedger.JsonOptions);
    private static ProjectDevelopmentLedger Ledger()=>new(){ProjectId=0,SourceHead=new string('a',40),CapturedAt=DateTimeOffset.UtcNow,
        Participants=[new(){Name="现有成员",UserName="other",Responsibility="保留真实账号"},new(){Name="待核实参与人",UserName="missing",Responsibility="不创建账号"}],
        Changes=[new(){Commit=new string('a',40),Author="原始 Git 署名",Subject="真实代码修改",Message="不是系统执行记录",AuthoredAt=DateTimeOffset.UtcNow,CommittedAt=DateTimeOffset.UtcNow,FileCount=1,Files=["ToDo.Domain/service.cs"]}]};
    private sealed class Bundle
    {
        public string Root="";
        public ProjectHistoryBackfillManifest Manifest=new(){ProjectName="撒豆成兵（正式开发）",LeaderUserName="admin",
            Timeline=[new(){Key="phase-2026-proof",Period="2026-10",SortDate=new(2026,10,1),Title="可核查阶段",Summary="只记有来源的历史",Sources="Git: aaaaaaaa"}]};
        public void Add(string name,string content)
        {
            var bytes=Encoding.UTF8.GetBytes(content);var path=Guid.NewGuid().ToString("N")+".bin";File.WriteAllBytes(Path.Combine(Root,path),bytes);
            Manifest.Files.Add(new(){Name=name,Path=path,Category="历史资料",Description="有来源，不推断旧状态",Hash=Convert.ToHexString(SHA256.HashData(bytes))});
        }
        public static Bundle Create(ProjectDocumentWorkflowTests.Fixture f,bool badArchive=false)
        {
            var b=new Bundle{Root=Path.Combine(f.Environment.ContentRootPath,"bundle")};Directory.CreateDirectory(b.Root);
            b.Add(ProjectDevelopmentLedger.FileName,Json(Ledger()));
            var body="原站旧任务内容，未改成当前任务";
            b.Add(ProjectArchivePackage.FileName,badArchive?"broken":Json(new ProjectArchivePackage{FormatVersion=1,ProjectId=20,RecordedAt=new DateTimeOffset(2026,10,13,0,0,0,TimeSpan.Zero),
                Records=[new(){Key="old14-task1",Kind="task",Title="旧任务",SourceLabel="原站项目14",SourceUrl="/Tasks/Details/1",Body=body,ContentHash=ProjectArchivePackage.HashBody(body)}]}));
            b.Add("需求.md","# 保留来源的项目资料");return b;
        }
    }

    [Fact]
    public async Task BackfillRemapsIdsAndMembers_PreservesOriginsAndDoesNotCreateLiveWork()
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();var bundle=Bundle.Create(f);
        var result=await new ProjectHistoryBackfillService(f.Context,f.Documents).ApplyAsync(bundle.Root,bundle.Manifest,1);
        Assert.True(result.CreatedProject);Assert.NotEqual(20,result.ProjectId);Assert.Equal(1,result.TimelineAdded);Assert.Equal(3,result.DocumentsAdded);
        Assert.Equal(["missing"],result.UnmatchedPeople);Assert.Equal(2,await f.Context.Users.CountAsync());
        Assert.Equal(2,await f.Context.ProjectUsers.CountAsync(m=>m.ProjectId==result.ProjectId));
        Assert.Empty(await f.Context.ToDoTasks.ToListAsync());Assert.Empty(await f.Context.MeetingMinutes.ToListAsync());Assert.Empty(await f.Context.UserNotifications.ToListAsync());
        var ledger=await new ProjectDevelopmentLedgerReader(f.Context,f.Documents).ReadAsync(result.ProjectId,1);
        Assert.Equal(result.ProjectId,ledger!.Value.Ledger.ProjectId);Assert.Single(ledger.Value.Ledger.Changes);
        var doc=await f.Context.ProjectDocuments.SingleAsync(d=>d.ProjectId==result.ProjectId&&d.FileName==ProjectArchivePackage.FileName);
        var archive=ProjectArchivePackage.Parse(await File.ReadAllTextAsync(f.Documents.ResolveStoragePath(doc.StoragePath)),result.ProjectId);
        Assert.Empty(Assert.Single(archive.Records).SourceUrl);Assert.Contains("原站",archive.Records[0].SourceLabel);
        var log=Assert.Single(await f.Context.ChangeLogs.ToListAsync());Assert.StartsWith("[历史补录]",log.AfterContent);Assert.Equal(1,log.OperatedByUserId);
        Assert.True(log.OperatedAt>new DateTime(2026,10,13));
    }

    [Fact]
    public async Task SameBundleIsIdempotent_AndRecoversMissingFileWithoutDuplicateVersion()
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();var bundle=Bundle.Create(f);var service=new ProjectHistoryBackfillService(f.Context,f.Documents);
        var first=await service.ApplyAsync(bundle.Root,bundle.Manifest,1);
        var again=await service.ApplyAsync(bundle.Root,bundle.Manifest,1);Assert.False(again.CreatedProject);Assert.Equal(0,again.DocumentsAdded);Assert.Equal(3,again.Skipped);
        Assert.Single(await f.Context.ChangeLogs.ToListAsync());
        var doc=await f.Context.ProjectDocuments.SingleAsync(d=>d.ProjectId==first.ProjectId&&d.FileName=="需求.md");File.Delete(f.Documents.ResolveStoragePath(doc.StoragePath));
        var repaired=await service.ApplyAsync(bundle.Root,bundle.Manifest,1);Assert.Equal(1,repaired.FilesRestored);Assert.Equal(0,repaired.DocumentsAdded);
        Assert.Equal("# 保留来源的项目资料",await File.ReadAllTextAsync(f.Documents.ResolveStoragePath(doc.StoragePath)));
        Assert.Equal(3,await f.Context.ProjectDocuments.CountAsync(d=>d.ProjectId==first.ProjectId));
    }

    [Fact]
    public async Task InvalidLateArchiveRollsBackProjectMembersNodesDocumentsAndCreatedFiles()
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();var bundle=Bundle.Create(f,true);
        await Assert.ThrowsAsync<JsonException>(()=>new ProjectHistoryBackfillService(f.Context,f.Documents).ApplyAsync(bundle.Root,bundle.Manifest,1));
        Assert.Equal(2,await f.Context.Project.CountAsync());Assert.Empty(await f.Context.ProjectHistoryEntries.ToListAsync());Assert.Empty(await f.Context.ProjectDocuments.ToListAsync());Assert.Empty(await f.Context.ProjectUsers.ToListAsync());
        var store=Path.Combine(f.Environment.ContentRootPath,"App_Data","ProjectDocuments");
        Assert.Empty(Directory.Exists(store)?Directory.GetFiles(store,"*",SearchOption.AllDirectories):[]);
    }

    [Fact]
    public async Task TamperedSourceHashAndUnprivilegedActorFailBeforeDataChanges()
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();var bundle=Bundle.Create(f);var service=new ProjectHistoryBackfillService(f.Context,f.Documents);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ApplyAsync(bundle.Root,bundle.Manifest,2));
        File.AppendAllText(Path.Combine(bundle.Root,bundle.Manifest.Files[0].Path),"tampered");
        await Assert.ThrowsAsync<ArgumentException>(()=>service.ApplyAsync(bundle.Root,bundle.Manifest,1));
        Assert.Equal(2,await f.Context.Project.CountAsync());Assert.Empty(await f.Context.ProjectDocuments.ToListAsync());
    }

    [Fact]
    public async Task LedgerReaderIsProjectScopedAndRejectsDisabledUsers()
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();var bundle=Bundle.Create(f);
        bundle.Manifest.Files.RemoveAll(file=>file.Name==ProjectArchivePackage.FileName);var result=await new ProjectHistoryBackfillService(f.Context,f.Documents).ApplyAsync(bundle.Root,bundle.Manifest,1);
        Assert.Null(await new ProjectDevelopmentLedgerReader(f.Context,f.Documents).ReadAsync(11,1));
        (await f.Context.Users.FindAsync(2))!.Status=UserStatus.Inactive;await f.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>new ProjectDevelopmentLedgerReader(f.Context,f.Documents).ReadAsync(result.ProjectId,2));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("duplicate")]
    [InlineData("version")]
    [InlineData("count")]
    public void MalformedLedgerCannotMasqueradeAsAnotherProject(string reason)
    {
        var ledger=Ledger();ledger.ProjectId=10;
        if(reason=="duplicate")ledger.Changes.Add(ledger.Changes[0]);if(reason=="version")ledger.FormatVersion=99;if(reason=="count")ledger.Changes[0].FileCount=0;
        Assert.Throws<ArgumentException>(()=>ProjectDevelopmentLedger.Parse(Json(ledger),reason=="project"?11:10));
    }

    [Fact]
    public async Task MissingLedgerCanBeRestored_ButPresentTamperedLedgerCannotBeRead()
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();var bundle=Bundle.Create(f);var service=new ProjectHistoryBackfillService(f.Context,f.Documents);
        var first=await service.ApplyAsync(bundle.Root,bundle.Manifest,1);
        var document=await f.Context.ProjectDocuments.SingleAsync(d=>d.ProjectId==first.ProjectId&&d.FileName==ProjectDevelopmentLedger.FileName);
        var path=f.Documents.ResolveStoragePath(document.StoragePath);File.Delete(path);
        var repaired=await service.ApplyAsync(bundle.Root,bundle.Manifest,1);Assert.Equal(1,repaired.FilesRestored);Assert.Equal(0,repaired.DocumentsAdded);
        var reader=new ProjectDevelopmentLedgerReader(f.Context,f.Documents);Assert.NotNull(await reader.ReadAsync(first.ProjectId,1));
        await File.AppendAllTextAsync(path," ");await Assert.ThrowsAsync<ArgumentException>(()=>reader.ReadAsync(first.ProjectId,1));
    }

    [Fact]
    public async Task ReimportOlderBundlePreservesNewerGitEvidence()
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();var bundle=Bundle.Create(f);var service=new ProjectHistoryBackfillService(f.Context,f.Documents);
        var first=await service.ApplyAsync(bundle.Root,bundle.Manifest,1);
        var old=await new ProjectDevelopmentLedgerReader(f.Context,f.Documents).ReadAsync(first.ProjectId,1);var ledger=old!.Value.Ledger;
        ledger.SourceHead=new string('b',40);ledger.Changes.Add(new(){Commit=new string('b',40),Author="后续贡献者",Subject="后续修改",AuthoredAt=DateTimeOffset.UtcNow,CommittedAt=DateTimeOffset.UtcNow});
        var bytes=Encoding.UTF8.GetBytes(Json(ledger));await using var input=new MemoryStream(bytes);
        await f.Documents.SaveAsync(first.ProjectId,1,new Microsoft.AspNetCore.Http.FormFile(input,0,bytes.Length,"file",ProjectDevelopmentLedger.FileName),"后来追加的真实来源");
        var again=await service.ApplyAsync(bundle.Root,bundle.Manifest,1);Assert.Equal(0,again.DocumentsAdded);
        var preserved=await new ProjectDevelopmentLedgerReader(f.Context,f.Documents).ReadAsync(first.ProjectId,1);Assert.Equal(2,preserved!.Value.Ledger.Changes.Count);Assert.Equal(new string('b',40),preserved.Value.Ledger.SourceHead);
    }

    [Fact]
    public async Task ArchivedTargetIsNeverReactivatedByBackfill()
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();var bundle=Bundle.Create(f);var service=new ProjectHistoryBackfillService(f.Context,f.Documents);
        var first=await service.ApplyAsync(bundle.Root,bundle.Manifest,1);var project=(await f.Context.Project.FindAsync(first.ProjectId))!;
        project.Status=ProjectStatus.Archived;await f.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ApplyAsync(bundle.Root,bundle.Manifest,1));
        Assert.Equal(ProjectStatus.Archived,(await f.Context.Project.FindAsync(first.ProjectId))!.Status);
        Assert.Equal(3,await f.Context.ProjectDocuments.CountAsync(d=>d.ProjectId==first.ProjectId));
    }

    [Theory]
    [InlineData("../secret.json")]
    [InlineData("C:/secret.json")]
    public void BundlePathCannotEscapeItsRoot(string path)=>Assert.Throws<ArgumentException>(()=>ProjectHistoryBackfillService.ResolveBundlePath(Path.GetTempPath(),path));

    [Theory]
    [InlineData(ProjectDevelopmentLedger.FileName)]
    [InlineData(ProjectArchivePackage.FileName)]
    public async Task OrdinaryMembersCannotReplaceOrRollBackHistoricalEvidence(string fileName)
    {
        await using var f=await ProjectDocumentWorkflowTests.Fixture.CreateAsync();var bundle=Bundle.Create(f);
        var result=await new ProjectHistoryBackfillService(f.Context,f.Documents).ApplyAsync(bundle.Root,bundle.Manifest,1);
        var original=await f.Context.ProjectDocuments.SingleAsync(d=>d.ProjectId==result.ProjectId&&d.FileName==fileName);
        var bytes=Encoding.UTF8.GetBytes("{\"forged\":true}");
        Microsoft.AspNetCore.Http.FormFile Upload()=>new(new MemoryStream(bytes),0,bytes.Length,"file",fileName);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Documents.SaveAsync(result.ProjectId,2,Upload(),"伪造历史"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Documents.SaveWithCategoryAsync(result.ProjectId,2,Upload(),"伪造历史"));
        var updated=await f.Documents.SaveAsync(result.ProjectId,1,Upload(),"管理员维护测试");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Documents.RollbackVersionAsync(result.ProjectId,original.Id,2));
        Assert.Equal(updated.Id,(await f.Context.ProjectDocuments.AsNoTracking().SingleAsync(d=>d.ProjectId==result.ProjectId&&d.FileName==fileName&&d.IsCurrent)).Id);
    }
}

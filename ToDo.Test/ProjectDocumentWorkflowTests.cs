using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;
using ToDo.Razor.Pages.Projects;

namespace ToDo.Test;

public class ProjectDocumentWorkflowTests
{
    internal static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lN8AAAAASUVORK5CYII=");

    [Fact]
    public async Task Word_UploadAndTextExtraction_PreserveOriginalBytesAndTableOrder()
    {
        await using var f = await Fixture.CreateAsync();
        var bytes = Word(); var source = await f.Upload("需求'文档.docx", bytes);
        Assert.Equal(bytes, await System.IO.File.ReadAllBytesAsync(f.Documents.ResolveStoragePath(source.StoragePath)));
        var text = await ProjectDocumentIndexService.ReadContentAsync(f.Documents.ResolveStoragePath(source.StoragePath), source.FileName);
        Assert.True(text.CanUseForAI); Assert.Contains("不是原件", text.Notice);
        Assert.Contains("甲列\t乙列", text.Content);
        Assert.True(text.Content.IndexOf("开头", StringComparison.Ordinal) < text.Content.IndexOf("甲列", StringComparison.Ordinal));
        Assert.True(text.Content.IndexOf("乙列", StringComparison.Ordinal) < text.Content.IndexOf("结尾", StringComparison.Ordinal));
        Assert.Equal(bytes, await System.IO.File.ReadAllBytesAsync(f.Documents.ResolveStoragePath(source.StoragePath)));
    }

    [Fact]
    public async Task Word_Revision_IsRealMarkdownWithProvenance_OriginalRemainsCurrent()
    {
        await using var f = await Fixture.CreateAsync();
        var bytes = Word(); var source = await f.Upload("需求.docx", bytes);
        var revised = await f.Documents.SaveRevisionAsync(10, source.Id, 1, "# 更新后的需求", "补充验收标准");
        Assert.Equal("需求_AI修订.md", revised.FileName); Assert.EndsWith(".md", revised.StoragePath);
        Assert.Contains($"#{source.Id} v1", revised.Description);
        await f.Context.Entry(source).ReloadAsync(); Assert.True(source.IsCurrent);
        Assert.Equal(bytes, await System.IO.File.ReadAllBytesAsync(f.Documents.ResolveStoragePath(source.StoragePath)));
        Assert.Equal("# 更新后的需求", await System.IO.File.ReadAllTextAsync(f.Documents.ResolveStoragePath(revised.StoragePath)));
        Assert.NotNull(revised.FileHash);
    }

    [Fact]
    public async Task TextRevision_RejectsStaleSource_AndHandlesRevisionAfterRollback()
    {
        await using var f = await Fixture.CreateAsync();
        var source = await f.Upload("需求.md", Encoding.UTF8.GetBytes("v1"));
        var v2 = await f.Documents.SaveRevisionAsync(10, source.Id, 1, "v2", "修订");
        Assert.False(source.IsCurrent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Documents.SaveRevisionAsync(10, source.Id, 1, "过期覆盖", ""));
        await f.Documents.RollbackVersionAsync(10, source.Id, 1);
        var v3 = await f.Documents.SaveRevisionAsync(10, source.Id, 1, "v3", "回滚后继续修订");
        Assert.Equal(3, v3.VersionNumber);
        Assert.Equal(v3.Id, (await f.Context.ProjectDocuments.AsNoTracking().Where(d => d.IsCurrent).SingleAsync()).Id);
        Assert.Equal("v1", await System.IO.File.ReadAllTextAsync(f.Documents.ResolveStoragePath(source.StoragePath)));
        Assert.Equal(2, (await f.Documents.GetVersionHistoryAsync(10, v3.Id)).Count);
    }

    [Theory]
    [InlineData("fake.docx")]
    [InlineData("fake.pdf")]
    [InlineData("fake.html")]
    public async Task GeneratedText_CannotImpersonateBinaryOrActiveFormats(string name)
    {
        await using var f = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Documents.SaveGeneratedTextAsync(10, 1, name, "正文", ""));
        Assert.Empty(await f.Context.ProjectDocuments.ToListAsync());
    }

    [Fact]
    public async Task FailedExtractionAndLongText_AreNotPassedToAI()
    {
        await using var f = await Fixture.CreateAsync();
        foreach (var (name, bytes) in new[] { ("bad.docx", Encoding.UTF8.GetBytes("broken archive")), ("archive.bin", new byte[] {1,2}), ("large.md", Encoding.UTF8.GetBytes(new string('字',100001))) })
        {
            var doc = await f.Upload(name, bytes);
            var content = await ProjectDocumentIndexService.ReadContentAsync(f.Documents.ResolveStoragePath(doc.StoragePath), name);
            Assert.False(content.CanUseForAI);
            if (name == "large.md") Assert.True(content.IsTruncated);
            else Assert.Empty(content.Content);
        }
    }

    [Fact]
    public async Task Revision_DoesNotCrossProjectOrCategory_AndArchivedProjectIsReadOnly()
    {
        await using var f = await Fixture.CreateAsync();
        var source = await f.Upload("规范.md", Encoding.UTF8.GetBytes("旧规范"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Documents.SaveGeneratedTextAsync(10,1,"规范.md","新规范","",category:ProjectDocumentCategory.TechnicalMaterials));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Documents.SaveRevisionAsync(11,source.Id,1,"跨项目",""));
        (await f.Context.Project.FindAsync(10))!.Status = ProjectStatus.Archived; await f.Context.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Documents.SaveRevisionAsync(10,source.Id,1,"归档修改",""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Documents.RollbackVersionAsync(10,source.Id,1));
        Assert.True((await f.Context.ProjectDocuments.SingleAsync()).IsCurrent);
    }

    [Fact]
    public async Task AiRevise_UsesEnabledAgentsModel_ApprovalPreservesOfficeOriginal()
    {
        await using var f = await Fixture.CreateAsync();
        var source = await f.Upload("设计.docx", Word());
        await f.Grant();
        var page = f.Page(); await page.OnPostAiReviseAsync(source.Id,"补充测试标准");
        Assert.Equal("fixture-text-model", f.AI.Options?.ModelName);
        Assert.Contains("甲列", f.AI.Prompt);
        var draft = await f.Context.AiDocumentDrafts.SingleAsync(); Assert.EndsWith(".md", draft.DraftFileName);
        Assert.True(source.IsCurrent);
        await page.OnPostAuditDraftAsync(draft.Id,"pass");
        Assert.Equal(DraftAuditStatus.Pass, (await f.Context.AiDocumentDrafts.AsNoTracking().SingleAsync()).AuditStatus);
        Assert.Equal(2, await f.Context.ProjectDocuments.CountAsync());
        Assert.True((await f.Context.ProjectDocuments.AsNoTracking().SingleAsync(d=>d.Id==source.Id)).IsCurrent);
        await page.OnPostAuditDraftAsync(draft.Id,"pass");
        Assert.Equal(2, await f.Context.ProjectDocuments.CountAsync());
    }

    [Fact]
    public async Task AuditDraft_RevokedPermission_DoesNotPublishOrConsumeDraft()
    {
        await using var f = await Fixture.CreateAsync(); var source=await f.Upload("需求.md",Encoding.UTF8.GetBytes("需求"));
        await f.Grant(); var page=f.Page(); await page.OnPostAiReviseAsync(source.Id,"补充标准");
        var draft=await f.Context.AiDocumentDrafts.SingleAsync();
        (await f.Context.AgentDocumentPermissions.SingleAsync()).CanWrite=false;await f.Context.SaveChangesAsync();
        await page.OnPostAuditDraftAsync(draft.Id,"pass");
        Assert.Contains("权限", page.TempData["ErrorMessage"]?.ToString());
        Assert.Equal(DraftAuditStatus.Pending,(await f.Context.AiDocumentDrafts.AsNoTracking().SingleAsync()).AuditStatus);
        Assert.Single(await f.Context.ProjectDocuments.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ImageRevise_TextOnlyModelMakesNoCall_VisionModelReceivesAuthorizedPixels()
    {
        await using var f=await Fixture.CreateAsync();var source=await f.Upload("流程.png",Png);await f.Grant();var page=f.Page();
        await page.OnPostAiReviseAsync(source.Id,"根据流程写需求"); Assert.Equal(0,f.AI.Calls);
        Assert.Contains("图片能力",page.TempData["ErrorMessage"]?.ToString());
        f.Configuration["AI:VisionModels:0"]="fixture-text-model";
        await page.OnPostAiReviseAsync(source.Id,"根据流程写需求"); Assert.Equal(1,f.AI.Calls);
        Assert.Equal(Png,Assert.Single(f.AI.Options!.Images).Data);
        Assert.Single(await f.Context.AiDocumentDrafts.ToListAsync());
        var access=new AgentDocumentAccessService(f.Context,f.Environment,f.Configuration);
        Assert.Single(await access.ReadImagesAsync(10,f.Agent.AgentKey,1,f.Agent.ModelName));
        (await f.Context.AgentDocumentPermissions.SingleAsync()).CanRead=false;await f.Context.SaveChangesAsync();
        Assert.Empty(await access.ReadImagesAsync(10,f.Agent.AgentKey,1,f.Agent.ModelName));
    }

    [Fact]
    public async Task PreviewAndDraftApproval_EnforceUserAndFormatBoundaries()
    {
        await using var f=await Fixture.CreateAsync();await f.Grant();
        var doc=await f.Upload("evil.png",Encoding.UTF8.GetBytes("<script>alert(1)</script>"));
        Assert.IsType<BadRequestObjectResult>(await f.Page().OnGetOriginalPreviewAsync(doc.Id));
        doc=await f.Upload("真实.png",Png);
        var preview=Assert.IsType<PhysicalFileResult>(await f.Page().OnGetOriginalPreviewAsync(doc.Id));
        Assert.Equal("image/png",preview.ContentType);Assert.True(preview.EnableRangeProcessing);
        Assert.IsType<ForbidResult>(await f.Page(2).OnGetOriginalPreviewAsync(doc.Id));
        var text=await f.Upload("授权.md",Encoding.UTF8.GetBytes("原文"));await f.Page().OnPostAiReviseAsync(text.Id,"修改");
        var draft=await f.Context.AiDocumentDrafts.SingleAsync();
        f.Context.ProjectUsers.Add(new ProjectUser{ProjectId=10,UserId=2,ProjectRole=(int)ProjectRole.Member});await f.Context.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await f.Page(2).OnPostAuditDraftAsync(draft.Id,"pass"));
    }

    [Fact]
    public async Task AiClient_VisionOptInAndPayload_AreCheckedBeforeNetwork()
    {
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            ["AI:ApiKey"]="test-only",["AI:ApiUrl"]="https://fixture.invalid/chat/completions",["AI:ModelName"]="fixture-text-model"}).Build();
        var handler=new CaptureHandler(); var client=new AIService(configuration,new HttpClient(handler));
        var options=new AIChatOptions{Images=[new AIImageInput{FileName="流程.png",MediaType="image/png",Data=Png}]};
        var unsupported=await Assert.ThrowsAsync<Exception>(()=>client.GetChatCompletionWithUsageAsync("test",options));
        Assert.IsType<InvalidOperationException>(unsupported.InnerException);Assert.Equal(0,handler.Calls);
        configuration["AI:VisionModels:0"]="fixture-text-model";
        await client.GetChatCompletionWithUsageAsync("test",options);Assert.Equal(1,handler.Calls);
        using var body=JsonDocument.Parse(handler.Body!);var parts=body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("text",parts[0].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,",parts[2].GetProperty("image_url").GetProperty("url").GetString());
        options.Images=[new AIImageInput{FileName="伪装.png",MediaType="image/png",Data=Encoding.UTF8.GetBytes("not png")}];
        var invalid=await Assert.ThrowsAsync<Exception>(()=>client.GetChatCompletionWithUsageAsync("test",options));
        Assert.IsType<InvalidOperationException>(invalid.InnerException);Assert.Equal(1,handler.Calls);
    }

    private static byte[] Word()
    {
        using var stream=new MemoryStream();
        using(var doc=WordprocessingDocument.Create(stream,WordprocessingDocumentType.Document))
        {
            var part=doc.AddMainDocumentPart();
            var body=new Body();
            body.Append(new Paragraph(new Run(new RunProperties(new Bold()),new Text("开头需求"))));
            var row=new TableRow();
            row.Append(new TableCell(new Paragraph(new Run(new Text("甲列")))));
            row.Append(new TableCell(new Paragraph(new Run(new Text("乙列")))));
            body.Append(new Table(row));
            body.Append(new Paragraph(new Run(new Text("结尾验收"))));
            part.Document=new Document(body);part.Document.Save();
        }
        return stream.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormalApproval_CustomCategoryRevision_RechecksGrant(bool revoked)
    {
        await using var f=await Fixture.CreateAsync();var source=await f.Upload("自定义资料.docx",Word());
        var category=new DocumentCategory{ProjectId=10,Name="产品设计"};f.Context.DocumentCategories.Add(category);await f.Context.SaveChangesAsync();
        source.CategoryId=category.Id;source.CategoryName=category.Name;await f.Context.SaveChangesAsync();
        await f.Grant();var grant=await f.Context.AgentDocumentPermissions.SingleAsync();grant.CategoryId=category.Id;await f.Context.SaveChangesAsync();
        var reviewer=(await f.Context.Users.FindAsync(2))!;reviewer.Role=UserRole.systemAdmin;await f.Context.SaveChangesAsync();
        var service=new ApprovalRequestService(f.Context,f.Documents,new UserNotificationService(f.Context),new AgentDocumentAccessService(f.Context,f.Environment));
        var request=await service.RequestAsync(10,1,"document-regression",source.Id,ApprovalRequestService.AgentDocumentWrite,"修订自定义资料",
            JsonSerializer.Serialize(new AgentDocumentWritePayload{AgentKey=f.Agent.AgentKey,SourceDocumentId=source.Id,CategoryId=category.Id,Category=source.Category,Content="# 新需求"}),notifyApprovers:false);
        if(revoked){grant.CanWrite=false;await f.Context.SaveChangesAsync();await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ApproveAsync(request.Id,reviewer,"验收"));}
        else
        {
            await service.ApproveAsync(request.Id,reviewer,"验收");
            var revision=await f.Context.ProjectDocuments.AsNoTracking().SingleAsync(d=>d.Id!=source.Id);
            Assert.Equal(category.Id,revision.CategoryId);Assert.EndsWith(".md",revision.FileName);
        }
        Assert.True((await f.Context.ProjectDocuments.AsNoTracking().SingleAsync(d=>d.Id==source.Id)).IsCurrent);
        Assert.Equal(revoked?1:2,await f.Context.ProjectDocuments.CountAsync());
    }

    private sealed class CaptureHandler:HttpMessageHandler
    {
        public int Calls;public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        { Calls++;Body=await request.Content!.ReadAsStringAsync(ct);return new(HttpStatusCode.OK){Content=new StringContent("{\"choices\":[{\"message\":{\"content\":\"已识别\"}}]}")}; }
    }

    public class LocalAI:DispatchProxy
    {
        public int Calls;public string? Prompt;public AIChatOptions? Options;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        { Calls++;Prompt=args?[0] as string;Options=args?.ElementAtOrDefault(1) as AIChatOptions;return Task.FromResult("# 修订稿\n补充验收标准。"); }
    }
    private sealed class Registry(AgentDefinition agent):IAgentRegistry
    {
        public Task<IReadOnlyList<AgentDefinition>> GetAllAsync(CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<AgentDefinition>>([agent]);
        public Task<AgentDefinition?> GetAsync(string key,CancellationToken ct=default)=>Task.FromResult<AgentDefinition?>(key==agent.AgentKey?agent:null);
        public Task<AgentDefinition?> GetForExecutionAsync(string key,int? version=null,string? routingKey=null,CancellationToken cancellationToken=default)=>GetAsync(key,cancellationToken);
        public Task EnsureDefaultsAsync(CancellationToken ct=default)=>Task.CompletedTask;
        public Task ApplyManagedUpdateAsync(int id,int? user=null,CancellationToken ct=default)=>Task.CompletedTask;
    }
    private sealed class TempDataProvider:ITempDataProvider
    {
        public IDictionary<string,object> LoadTempData(HttpContext context)=>new Dictionary<string,object>();
        public void SaveTempData(HttpContext context,IDictionary<string,object> values){}
    }
    private sealed class Fixture:IAsyncDisposable
    {
        private SqliteConnection connection=null!;private ServiceProvider provider=null!;
        public ApplicationDbContext Context=null!;
        public ProjectDocumentIndexServiceTests.TestHostEnvironment Environment=new(){ContentRootPath=Path.Combine(Path.GetTempPath(),"todo-doc-workflow",Guid.NewGuid().ToString("N"))};
        public IConfigurationRoot Configuration=new ConfigurationBuilder().AddInMemoryCollection().Build();
        public ProjectDocumentService Documents=null!;
        public AgentDefinition Agent=new(){AgentKey="document-helper",Name="资料助手",ModelName="fixture-text-model",IsEnabled=true};
        public IAIService AIService=DispatchProxy.Create<IAIService,LocalAI>();
        public LocalAI AI=> (LocalAI)(object)AIService;
        public static async Task<Fixture> CreateAsync()
        {
            var f=new Fixture();f.connection=new("Data Source=:memory:");await f.connection.OpenAsync();
            f.Context=new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(f.connection).Options);await f.Context.Database.EnsureCreatedAsync();
            f.Context.Users.AddRange(new ApplicationUser{Id=1,UserName="admin",Role=UserRole.systemAdmin,Status=UserStatus.Active},new ApplicationUser{Id=2,UserName="other",Role=UserRole.teamMember,Status=UserStatus.Active});
            await f.Context.SaveChangesAsync();f.Context.Project.AddRange(new Project{Id=10,Name="正式资料测试",LeaderUserId=1,CreatedByUserId=1},new Project{Id=11,Name="另一项目",LeaderUserId=1,CreatedByUserId=1});
            f.Context.AgentDefinitions.Add(f.Agent);await f.Context.SaveChangesAsync();f.Documents=new(f.Context,f.Environment);
            var services=new ServiceCollection();services.AddLogging();services.AddSingleton(f.Context);services.AddIdentity<ApplicationUser,IdentityRole<int>>().AddEntityFrameworkStores<ApplicationDbContext>();f.provider=services.BuildServiceProvider();return f;
        }
        public Task<ProjectDocument> Upload(string name,byte[] bytes)=>Documents.SaveAsync(10,1,new FormFile(new MemoryStream(bytes),0,bytes.Length,"UploadFile",name),"");
        public async Task Grant(){Context.AgentDocumentPermissions.Add(new AgentDocumentPermission{ProjectId=10,AgentKey=Agent.AgentKey,Category=ProjectDocumentCategory.Other,CanRead=true,CanWrite=true,UpdatedById=1});await Context.SaveChangesAsync();}
        public MaterialsModel Page(int userId=1)
        {
            var page=new MaterialsModel(Context,provider.GetRequiredService<UserManager<ApplicationUser>>(),Documents,new(Context,Documents,NullLogger<ProjectDocumentIndexService>.Instance),new(Context),new(Context,Environment,Configuration),new Registry(Agent),AIService,new EventBusService(Context),NullLogger<MaterialsModel>.Instance,Configuration){ProjectId=10};
            var http=new DefaultHttpContext{User=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,userId.ToString())],"test"))};
            page.PageContext=new PageContext{HttpContext=http};page.TempData=new TempDataDictionary(http,new TempDataProvider());return page;
        }
        public async ValueTask DisposeAsync()
        {
            // All artifacts have unique fixture roots; only individually recorded files are removed.
            var paths=(await Context.ProjectDocuments.AsNoTracking().Select(d=>d.StoragePath).ToListAsync()).Concat(await Context.AiDocumentDrafts.AsNoTracking().Select(d=>d.DraftFilePath).ToListAsync());
            foreach(var path in paths.Distinct()){var full=Documents.ResolveStoragePath(path);if(System.IO.File.Exists(full))System.IO.File.Delete(full);}
            await provider.DisposeAsync();await connection.DisposeAsync();
        }
    }
}

using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;
using ToDo.Entities.DailySummary;

namespace ToDo.Test;

public class WorkReportAutomationTests
{
    [Fact]
    public async Task AgentCreatedReport_RemainsLabelledAiEvenWhenAHumanApprovedIt()
    {
        await using var f = await Fixture.CreateAsync();
        var approvals = new ApprovalRequestService(f.Context, f.Documents, new UserNotificationService(f.Context), null!);
        var request = await approvals.RequestAsync(10, 2, "report-origin-test", null,
            ApprovalRequestService.AgentDailyReportCreate, "创建Agent报告", System.Text.Json.JsonSerializer.Serialize(
                new AgentDailyReportCreatePayload { Title = "Agent日报", Content = "模型生成正文" }), notifyApprovers: false);
        await approvals.ApproveAsync(request.Id, f.Manager, "允许保存");
        var report = await f.Context.DailyReport.SingleAsync();
        Assert.True(report.IsSystemGenerated);
        Assert.Equal("模型生成正文", report.GeneratedContent);
        Assert.Equal("AI / 系统生成", report.ContentOrigin);
        Assert.Null(report.LastHumanEditedAt);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task AgentReportContext_RespectsMemberReportVisibility(int actorId, bool mayReadMemberReports)
    {
        await using var f = await Fixture.CreateAsync();
        f.Context.DailyReport.AddRange(
            new DailyReport { ProjectId = 10, ReporterId = 3, ReportDate = AppTime.Today, ReportType = 1,
                ReportTitle = "普通项目日报", ReportContent = "允许成员读取的项目进展" },
            new DailyReport { ProjectId = 10, ReporterId = 3, ReportDate = AppTime.Today, ReportType = 4,
                ReportTitle = "受限成员日报", ReportContent = "成员汇总私有正文" },
            new DailyReport { ProjectId = 10, ReporterId = 3, ReportDate = AppTime.Today, ReportType = 2,
                ReportTitle = "受限周报", ReportContent = "成员周报私有正文", ContainsMemberDailyData = true },
            new DailyReport { ProjectId = 20, ReporterId = 4, ReportDate = AppTime.Today, ReportType = 2,
                ReportTitle = "其他项目周报", ReportContent = "B项目不可混入上下文", ContainsMemberDailyData = true });
        await f.Context.SaveChangesAsync();
        f.Agent.ContextSourcesJson = "[\"Reports\"]";
        var builder = new AgentContextService(f.Context, null!);
        var result = await builder.BuildAsync(f.Agent, new AiSession { ProjectId = 10, UserId = actorId }, actorId, recordAudit: false);
        Assert.Contains("允许成员读取的项目进展", result);
        Assert.Equal(mayReadMemberReports, result.Contains("成员汇总私有正文"));
        Assert.Equal(mayReadMemberReports, result.Contains("成员周报私有正文"));
        Assert.DoesNotContain("B项目不可混入上下文", result);
        Assert.Equal(0, f.AI.Calls);
    }

    [Fact]
    public async Task ManualDaily_DoesNotRequireAnAiProvider()
    {
        await using var f = await Fixture.CreateAsync();
        var service = new WorkReportService(f.Context, null);
        var note = await service.SavePersonalNoteAsync(2, 10, AppTime.Today, "没有模型也能手写日报", 0);
        Assert.Equal(1, note.Revision);
        Assert.Single(await service.PersonalNotesAsync(2, 2, AppTime.Today));
        Assert.Equal(0, f.AI.Calls);
    }
    [Fact]
    public async Task ManualDaily_AppendOnlyRevisionAndStaleEditCannotOverwrite()
    {
        await using var f = await Fixture.CreateAsync();
        var first = await f.Reports.SavePersonalNoteAsync(2, 10, AppTime.Today.AddDays(-1), "人工初稿", 0);
        var second = await f.Reports.SavePersonalNoteAsync(2, 10, first.ReportDate, "人工校订", 1);
        Assert.Equal(2, second.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Reports.SavePersonalNoteAsync(2, 10, first.ReportDate, "过期输入", 1));
        Assert.Equal("人工初稿", (await f.Context.DailyReportSupplements.SingleAsync(n => n.Id == first.Id)).Content);
        Assert.Equal("人工校订", Assert.Single(await f.Reports.PersonalNotesAsync(2, 2, first.ReportDate)).Content);
        Assert.DoesNotContain(await f.Context.ChangeLogs.ToListAsync(), l => (l.AfterContent ?? "").Contains("人工初稿") || (l.BeforeContent ?? "").Contains("人工初稿"));
        Assert.Empty(await f.Context.DailyWorkSummaries.ToListAsync());
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("archived")]
    [InlineData("future")]
    [InlineData("inactive")]
    public async Task ManualDaily_RejectsInvalidScopeOrDate(string scenario)
    {
        await using var f = await Fixture.CreateAsync();
        var projectId = scenario == "foreign" ? 20 : 10;
        var date = scenario == "future" ? AppTime.Today.AddDays(1) : AppTime.Today;
        if (scenario == "archived") (await f.Context.Project.FindAsync(10))!.Status = ProjectStatus.Archived;
        if (scenario == "inactive") (await f.Context.Users.FindAsync(2))!.Status = UserStatus.Inactive;
        await f.Context.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Reports.SavePersonalNoteAsync(2, projectId, date, "输入", 0));
        Assert.Empty(await f.Context.DailyReportSupplements.ToListAsync());
        Assert.Equal(0, f.AI.Calls);
    }

    [Fact]
    public async Task ManualDaily_OtherMemberCannotReadHistory()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Reports.SavePersonalNoteAsync(2, 10, AppTime.Today, "私人项目内自述", 0);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Reports.PersonalNotesAsync(3, 2, null));
        Assert.Single(await f.Reports.PersonalNotesAsync(1, 2, null));
    }

    [Fact]
    public async Task MyDaily_NoGeneratedSummaryStillShowsManualAndHistoricalDays()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Reports.SavePersonalNoteAsync(2, 10, AppTime.Today.AddDays(-2), "补录工作", 0);
        var page = new ToDo.Razor.Pages.DailySummary.IndexModel(f.Context, NullLogger<ToDo.Razor.Pages.DailySummary.IndexModel>.Instance)
        { SelectDate = AppTime.Today };
        page.PageContext = new PageContext { HttpContext = f.Http(2) };
        await page.OnGetAsync();
        Assert.Empty(page.ErrorMessage);
        Assert.Null(page.Summary);
        Assert.Contains(page.SummaryHistoryList, s => s.Id == 0 && s.SummaryDate == AppTime.Today.AddDays(-2));
    }

    [Fact]
    public async Task MyDaily_LoadsSelectedDayAndIncludesLegacyTimestampsOnFilterEndDate()
    {
        await using var f = await Fixture.CreateAsync();
        var previousDay = AppTime.Today.AddDays(-1);
        var today = AppTime.Today;
        f.Context.DailyWorkSummaries.AddRange(
            new DailyWorkSummary { UserId = 2, Title = "昨天日报", TotalSummary = "昨天正文", SummaryDate = previousDay.AddHours(9) },
            new DailyWorkSummary { UserId = 2, Title = "今天日报", TotalSummary = "今天正文", SummaryDate = today });
        await f.Context.SaveChangesAsync();

        var page = new ToDo.Razor.Pages.DailySummary.IndexModel(f.Context, NullLogger<ToDo.Razor.Pages.DailySummary.IndexModel>.Instance)
        {
            SelectDate = previousDay,
            FilterStartDate = previousDay,
            FilterEndDate = previousDay,
            PageContext = new PageContext { HttpContext = f.Http(2) }
        };
        await page.OnGetAsync();
        Assert.Empty(page.ErrorMessage);
        Assert.Equal("昨天正文", page.Summary?.TotalSummary);
        Assert.Equal("昨天日报", Assert.Single(page.SummaryHistoryList).Title);

        page.SelectDate = today;
        await page.OnGetAsync();
        Assert.Empty(page.ErrorMessage);
        Assert.Equal("今天正文", page.Summary?.TotalSummary);
    }

    [Fact]
    public async Task Weekly_UsesOnlyProjectSourcesAndRetainsOriginalSnapshots()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Reports.SavePersonalNoteAsync(2, 10, AppTime.Today, "完成接口联调", 0);
        await f.Reports.SavePersonalNoteAsync(2, 10, AppTime.Today, "接口联调仍有异常，待修复", 1);
        var summary = new DailyWorkSummary { UserId = 2, SummaryDate = AppTime.Today, Title = "日报",
            TotalSummary = "其他项目机密总摘要", SummaryStatus = 1 };
        f.Context.DailyWorkSummaries.Add(summary); await f.Context.SaveChangesAsync();
        f.Context.DailyProjectSummaryDetails.AddRange(
            new DailyProjectSummaryDetail { DailySummaryId = summary.Id, ProjectId = 10, ProjectName = "A", ProjectSummary = "A项目记录" },
            new DailyProjectSummaryDetail { DailySummaryId = summary.Id, ProjectId = 20, ProjectName = "B", ProjectSummary = "B机密记录" });
        await f.Context.SaveChangesAsync();
        var report = await f.Reports.GenerateWeeklyAsync(3, 10, AppTime.Today.AddDays(-6), AppTime.Today);
        Assert.Contains("仍有异常", f.AI.Prompt); Assert.DoesNotContain("完成接口联调", f.AI.Prompt);
        Assert.DoesNotContain("其他项目机密", f.AI.Prompt); Assert.DoesNotContain("B机密", f.AI.Prompt);
        Assert.True(report.ContainsMemberDailyData); Assert.Equal(report.ReportContent, report.GeneratedContent);
        var source = Assert.Single(WorkReportService.ReadSources(report.SourceSnapshotJson), s => s.Kind == "人工日报");
        Assert.Equal(2, source.Revision);
        await f.Reports.SavePersonalNoteAsync(2, 10, AppTime.Today, "之后修订，不改变周报快照", 2);
        Assert.DoesNotContain("之后修订", report.SourceSnapshotJson!);
        Assert.False(await new DailyReportService(f.Context, null!).CanAccessReportAsync(report.Id, f.Member));
        Assert.True(await new DailyReportService(f.Context, null!).CanAccessReportAsync(report.Id, f.Manager));
    }

    [Fact]
    public async Task Weekly_ReuseDoesNotCallAiOrOverwriteHumanRevision_RegenerateCreatesAnotherReport()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Reports.SavePersonalNoteAsync(2, 10, AppTime.Today, "人工工作", 0);
        var first = await f.Reports.GenerateWeeklyAsync(3, 10, AppTime.Today, AppTime.Today);
        await new DailyReportService(f.Context, null!).UpdateDailyReport(first, first.ReportTitle, "管理人员人工校订", 2, first.ReportDate, f.Manager);
        Assert.Equal("AI 周报正文", first.GeneratedContent); Assert.NotNull(first.LastHumanEditedAt);
        var repeated = await f.Reports.GenerateWeeklyAsync(3, 10, AppTime.Today, AppTime.Today);
        Assert.Equal(first.Id, repeated.Id); Assert.Equal(1, f.AI.Calls);
        Assert.Equal("管理人员人工校订", repeated.ReportContent);
        var regenerated = await f.Reports.GenerateWeeklyAsync(3, 10, AppTime.Today, AppTime.Today, true);
        Assert.NotEqual(first.Id, regenerated.Id); Assert.Equal(2, f.AI.Calls);
        Assert.Equal(2, await f.Context.DailyReport.CountAsync());
    }

    [Fact]
    public async Task Weekly_StaleHumanEditCannotOverwriteLatestBody()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Reports.SavePersonalNoteAsync(2, 10, AppTime.Today, "人工工作", 0);
        var report = await f.Reports.GenerateWeeklyAsync(3, 10, AppTime.Today, AppTime.Today);
        var token = DailyReportService.ReportVersionToken(report);
        var service = new DailyReportService(f.Context, null!);
        await service.UpdateDailyReport(report, report.ReportTitle, "最新人工修改", 2, report.ReportDate, f.Manager, token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateDailyReport(report, report.ReportTitle,
            "过期内容", 2, report.ReportDate, f.Manager, token));
        Assert.Equal("最新人工修改", report.ReportContent);
        Assert.Equal("AI 周报正文", report.GeneratedContent);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("archived")]
    [InlineData("long-range")]
    [InlineData("future")]
    public async Task Weekly_InvalidRequestsCannotCallModel(string scenario)
    {
        await using var f = await Fixture.CreateAsync();
        if (scenario == "archived") { (await f.Context.Project.FindAsync(10))!.Status = ProjectStatus.Archived; await f.Context.SaveChangesAsync(); }
        var user = scenario == "member" ? 2 : 3;
        var start = scenario == "long-range" ? AppTime.Today.AddDays(-8) : AppTime.Today;
        var end = scenario == "future" ? AppTime.Today.AddDays(1) : AppTime.Today;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Reports.GenerateWeeklyAsync(user, 10, start, end));
        Assert.Equal(0, f.AI.Calls); Assert.Empty(await f.Context.DailyReport.ToListAsync());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("revoked")]
    [InlineData("length")]
    public async Task Weekly_ModelFailureOrPermissionChangeCannotPersist(string scenario)
    {
        await using var f = await Fixture.CreateAsync();
        await f.Reports.SavePersonalNoteAsync(2, 10, AppTime.Today, "资料核查", 0);
        if (scenario == "empty") f.AI.Response = "";
        else if (scenario == "length") f.AI.FinishReason = "length";
        else f.AI.BeforeReturn = async () => { f.Context.ProjectUsers.Remove(await f.Context.ProjectUsers.SingleAsync(p => p.ProjectId == 10 && p.UserId == 3)); await f.Context.SaveChangesAsync(); };
        await Assert.ThrowsAnyAsync<Exception>(() => f.Reports.GenerateWeeklyAsync(3, 10, AppTime.Today, AppTime.Today));
        Assert.Empty(await f.Context.DailyReport.ToListAsync());
    }

    [Fact]
    public async Task DocumentAi_ExplicitWriteSavesNewVersionAndRollbackPreservesBytes()
    {
        await using var f = await Fixture.CreateAsync();
        var doc = await f.UploadAsync(); await f.GrantAsync();
        var editor = new ProjectDocumentEditingService(f.Context, f.Documents);
        var source = await editor.ReadAsync(10, doc.Id, 0, 2);
        var aiEditor = new ProjectDocumentAiEditingService(f.Context, f.Documents, new Registry(f.Agent), f.AIService);
        f.AI.Response = "# 新正文\n补充验收标准";
        var result = await aiEditor.AdjustAsync(10, doc.Id, 2, source.Token, source.Content, "补充验收标准");
        Assert.True((await f.Context.ProjectDocuments.FindAsync(doc.Id))!.IsCurrent); // preview alone never writes a version
        await editor.SaveAsync(10, doc.Id, 0, 2, source.Token, result.Content, changeReason: $"AI调整；{result.AgentName}");
        var current = await f.Context.ProjectDocuments.SingleAsync(d => d.IsCurrent);
        Assert.Equal(2, current.VersionNumber); Assert.Equal("# 原文", await File.ReadAllTextAsync(f.Documents.ResolveStoragePath(doc.StoragePath)));
        await f.Documents.RollbackVersionAsync(10, doc.Id, 2);
        Assert.True((await f.Context.ProjectDocuments.FindAsync(doc.Id))!.IsCurrent);
        Assert.Equal("# 新正文\n补充验收标准", await File.ReadAllTextAsync(f.Documents.ResolveStoragePath(current.StoragePath)));
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("stale")]
    [InlineData("outsider")]
    public async Task DocumentAi_InvalidScopeCannotCallModel(string scenario)
    {
        await using var f = await Fixture.CreateAsync();
        var doc = await f.UploadAsync(); if (scenario != "grant") await f.GrantAsync();
        var snapshot = await new ProjectDocumentEditingService(f.Context, f.Documents).ReadAsync(10, doc.Id, 0, 2);
        await Assert.ThrowsAnyAsync<Exception>(() => new ProjectDocumentAiEditingService(f.Context, f.Documents, new Registry(f.Agent), f.AIService)
            .AdjustAsync(10, doc.Id, scenario == "outsider" ? 4 : 2, scenario == "stale" ? "stale" : snapshot.Token, snapshot.Content, "修订"));
        Assert.Equal(0, f.AI.Calls); Assert.Single(await f.Context.ProjectDocuments.ToListAsync());
    }

    [Theory]
    [InlineData("length")]
    [InlineData("empty")]
    [InlineData("revoked")]
    [InlineData("archived")]
    public async Task DocumentAi_TruncationOrPermissionRevocationDoesNotSaveVersion(string scenario)
    {
        await using var f = await Fixture.CreateAsync();
        var doc = await f.UploadAsync(); await f.GrantAsync();
        var snapshot = await new ProjectDocumentEditingService(f.Context, f.Documents).ReadAsync(10, doc.Id, 0, 2);
        if (scenario == "length") f.AI.FinishReason = "length";
        if (scenario == "empty") f.AI.Response = "";
        if (scenario == "revoked") f.AI.BeforeReturn = async () => { (await f.Context.AgentDocumentPermissions.SingleAsync()).CanWrite = false; await f.Context.SaveChangesAsync(); };
        if (scenario == "archived") f.AI.BeforeReturn = async () => { (await f.Context.Project.FindAsync(10))!.Status = ProjectStatus.Archived; await f.Context.SaveChangesAsync(); };
        await Assert.ThrowsAnyAsync<Exception>(() => new ProjectDocumentAiEditingService(f.Context, f.Documents, new Registry(f.Agent), f.AIService)
            .AdjustAsync(10, doc.Id, 2, snapshot.Token, snapshot.Content, "修订"));
        Assert.Single(await f.Context.ProjectDocuments.ToListAsync());
        Assert.Equal("# 原文", await File.ReadAllTextAsync(f.Documents.ResolveStoragePath(doc.StoragePath)));
    }

    [Fact]
    public void TextDiff_PreservesSeparatedChangesAndBoundsLargePreviews()
    {
        var result = DocumentTextDiff.Compare("# 标题\n旧句\n保留\n旧尾", "# 标题\n新句\n保留\n新尾");
        Assert.False(result.IsRangeComparison); Assert.Equal(4, result.Changes.Count);
        Assert.DoesNotContain(result.Changes, c => c.Text == "保留");
        var large = DocumentTextDiff.Compare(string.Join("\n", Enumerable.Repeat("旧", 1000)), string.Join("\n", Enumerable.Repeat("新", 1000)));
        Assert.True(large.IsRangeComparison); Assert.True(large.IsTruncated); Assert.Equal(200, large.Changes.Count);
    }

    [Fact]
    public async Task ManualDaily_OldSelectedDayIsNotHiddenByRecentHistoryLimit()
    {
        await using var f = await Fixture.CreateAsync();
        var date = AppTime.Today.AddDays(-250);
        await f.Reports.SavePersonalNoteAsync(2, 10, date, "早期日报", 0);
        for (var i = 0; i < 201; i++) f.Context.DailyReportSupplements.Add(new DailyReportSupplement { ProjectId = 10,
            UserId = 2, ReportDate = AppTime.Today.AddDays(-i), Revision = 1, Content = "近期日报" });
        await f.Context.SaveChangesAsync();
        Assert.Equal("早期日报", Assert.Single(await f.Reports.PersonalNotesAsync(2, 2, date)).Content);
        Assert.Single(await f.Reports.PersonalNotesAsync(2, 2, null, startDate: date, endDate: date));
    }

    [Fact]
    public async Task MemberSummary_IncludesManualNotesWithoutCrossProjectPersonalSummary()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Reports.SavePersonalNoteAsync(2, 10, AppTime.Today, "人工补充工作", 0);
        var summary = new DailyWorkSummary { UserId = 2, SummaryDate = AppTime.Today, Title = "个人日报", TotalSummary = "B项目秘密" };
        f.Context.DailyWorkSummaries.Add(summary); await f.Context.SaveChangesAsync();
        f.Context.DailyProjectSummaryDetails.Add(new DailyProjectSummaryDetail { DailySummaryId = summary.Id, ProjectId = 10,
            ProjectName = "A项目", ProjectSummary = "A系统记录" }); await f.Context.SaveChangesAsync();
        var result = await new PersonalDailySummaryService(f.Context, f.AIService, null!, null!, NullLogger<PersonalDailySummaryService>.Instance)
            .GenerateTeamReportAsync(10, AppTime.Today);
        Assert.Equal(1, result.GeneratedProjects);
        var member = Assert.Single(f.AI.TeamInput!.Members, m => m.HasDailyReport);
        Assert.Contains("人工补充工作", member.ProjectSummary);
        Assert.Contains("A系统记录", member.ProjectSummary);
        Assert.Empty(member.TotalSummary);
    }

    public class LocalAI : DispatchProxy
    {
        public int Calls;
        public string Prompt = string.Empty;
        public string Response = "AI 周报正文";
        public string FinishReason = "stop";
        public Func<Task>? BeforeReturn;
        public TeamReportInput? TeamInput;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls++; Prompt = args?[0] as string ?? string.Empty;
            if (method!.ReturnType == typeof(Task<AIDailyReportResult>))
            { TeamInput = (TeamReportInput)args![0]!; return Task.FromResult(new AIDailyReportResult { Success = true, GeneratedReport = Response }); }
            return method!.ReturnType == typeof(Task<AICompletionResult>) ? Completion() : Text();
        }
        private async Task<string> Text() { if (BeforeReturn != null) await BeforeReturn(); return Response; }
        private async Task<AICompletionResult> Completion() => new() { Content = await Text(), FinishReason = FinishReason };
    }
    private sealed class Registry(AgentDefinition agent) : IAgentRegistry
    {
        public Task<IReadOnlyList<AgentDefinition>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentDefinition>>([agent]);
        public Task<AgentDefinition?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<AgentDefinition?>(agent);
        public Task<AgentDefinition?> GetForExecutionAsync(string key, int? version = null, string? routingKey = null, CancellationToken cancellationToken = default) => GetAsync(key, cancellationToken);
        public Task EnsureDefaultsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplyManagedUpdateAsync(int id, int? changedByUserId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "ToDo.Test";
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "todo-report-doc-" + Guid.NewGuid().ToString("N"));
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class Fixture(SqliteConnection connection, ApplicationDbContext context) : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; } = context;
        public ApplicationUser Member { get; private set; } = null!;
        public ApplicationUser Manager { get; private set; } = null!;
        public AgentDefinition Agent { get; private set; } = null!;
        public IAIService AIService { get; } = DispatchProxy.Create<IAIService, LocalAI>();
        public LocalAI AI => (LocalAI)AIService;
        public WorkReportService Reports => new(Context, AIService);
        public ProjectDocumentService Documents { get; private set; } = null!;
        public DefaultHttpContext Http(int id) => new() { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test")) };
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var f = new Fixture(connection, context) { Documents = new(context, new TestEnvironment()) };
            for (var id = 1; id <= 4; id++) context.Users.Add(new ApplicationUser { Id = id, UserName = "user" + id, RealName = "成员" + id,
                Role = id == 1 ? UserRole.systemAdmin : UserRole.teamMember, Status = UserStatus.Active });
            await context.SaveChangesAsync();
            context.Project.AddRange(new Project { Id = 10, Name = "A项目", CreatedByUserId = 1, LeaderUserId = 3 },
                new Project { Id = 20, Name = "B项目", CreatedByUserId = 1, LeaderUserId = 4 });
            await context.SaveChangesAsync();
            context.ProjectUsers.AddRange(new ProjectUser { ProjectId = 10, UserId = 2, ProjectRole = (int)ProjectRole.Member },
                new ProjectUser { ProjectId = 10, UserId = 3, ProjectRole = (int)ProjectRole.Admin },
                new ProjectUser { ProjectId = 20, UserId = 4, ProjectRole = (int)ProjectRole.Admin });
            f.Agent = new AgentDefinition { AgentKey = "doc-editor", Name = "资料助手", IsEnabled = true };
            context.AgentDefinitions.Add(f.Agent); await context.SaveChangesAsync();
            f.Member = await context.Users.SingleAsync(u => u.Id == 2); f.Manager = await context.Users.SingleAsync(u => u.Id == 3);
            return f;
        }
        public Task<ProjectDocument> UploadAsync()
        {
            var bytes = Encoding.UTF8.GetBytes("# 原文");
            return Documents.SaveAsync(10, 2, new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "需求.md"), "测试原件");
        }
        public async Task GrantAsync()
        {
            Context.AgentDocumentPermissions.Add(new AgentDocumentPermission { ProjectId = 10, AgentKey = Agent.AgentKey,
                Category = ProjectDocumentCategory.Other, CanRead = true, CanWrite = true, UpdatedById = 1 });
            await Context.SaveChangesAsync();
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var path in await Context.ProjectDocuments.AsNoTracking().Select(d => d.StoragePath).ToListAsync())
            { var file = Documents.ResolveStoragePath(path); if (File.Exists(file)) File.Delete(file); }
            await Context.DisposeAsync(); await connection.DisposeAsync();
        }
    }
}

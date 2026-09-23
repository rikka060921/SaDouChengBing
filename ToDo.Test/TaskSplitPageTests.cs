using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;
using ToDo.Razor.Pages.Tasks;
using ToDo.Razor.Pages.Projects;
using System.Text.Json;

namespace ToDo.Test;

public sealed class TaskSplitPageTests
{
    // ArchiveEndpoint test removed: MeetingPreparation page was reworked
    // and no longer contains agenda archive logic.

    [Fact]
    public async Task FirstSplit_BindsOnlyItsOwnFieldsAndRendersEditableTasks()
    {
        await using var site = await SplitSite.StartAsync();
        var html = await site.SplitAsync();
        Assert.Equal(new[] { "FIRST", "SECOND", "THIRD" }, ReadTitles(html));
        Assert.True(Guid.TryParse(Field(html, "SubmissionId"), out var batch) && batch != Guid.Empty);
        Assert.Equal(1, site.AI.Calls);
    }

    [Fact]
    public async Task DeleteThenInvalidSave_PreservesOriginalIndicesAndRemainingTasks()
    {
        await using var site = await SplitSite.StartAsync();
        var html = await site.SplitAsync();
        var form = site.SaveForm(html);
        AddRow(form, 0, "FIRST", deleted: true);
        AddRow(form, 1, "");
        AddRow(form, 2, "THIRD");
        var response = await site.Client.PostAsync("/Tasks/SplitTask?handler=SaveAll", new FormUrlEncodedContent(form));
        var result = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "FIRST", "", "THIRD" }, ReadTitles(result));
        Assert.Contains("display:none", Regex.Match(result, "<tr id=\"task-row-1-0\"[^>]*>").Value);
        Assert.Equal("THIRD", Field(result, "ProjectTaskGroups[1].Tasks[2].Title"));
        Assert.Equal(Field(html, "SubmissionId"), Field(result, "SubmissionId"));
        Assert.Equal(0, await site.CountTasksAsync());

        form["ProjectTaskGroups[1].Tasks[1].Title"] = "SECOND_FIXED";
        var saved = await site.Client.PostAsync("/Tasks/SplitTask?handler=SaveAll", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal(2, await site.CountTasksAsync());
    }

    [Fact]
    public async Task RepeatedSave_CreatesOneBatchAndOneTask()
    {
        await using var site = await SplitSite.StartAsync();
        var form = site.SaveForm(await site.SplitAsync());
        AddRow(form, 0, "ONCE");
        for (var i = 0; i < 2; i++)
        {
            var response = await site.Client.PostAsync("/Tasks/SplitTask?handler=SaveAll", new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }
        Assert.Equal(1, await site.CountTasksAsync());
    }

    [Fact]
    public async Task SaveWithoutBatch_DoesNotCreateTasks()
    {
        await using var site = await SplitSite.StartAsync();
        var form = site.SaveForm(await site.SplitAsync());
        form.Remove("SubmissionId");
        AddRow(form, 0, "INVALID");
        var response = await site.Client.PostAsync("/Tasks/SplitTask?handler=SaveAll", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("批次已失效", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        Assert.Equal(0, await site.CountTasksAsync());
    }

    [Fact]
    public async Task TruncatedSplit_KeepsCompleteTasksAndShowsWarning()
    {
        await using var site = await SplitSite.StartAsync();
        site.AI.Output = "[{\"title\":\"FIRST\",\"projectId\":1},{\"title\":\"unfinished";
        var html = await site.SplitAsync();
        Assert.Equal(new[] { "FIRST" }, ReadTitles(html));
        Assert.Contains("AI 输出被截断", WebUtility.HtmlDecode(html));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(999, false)]
    public async Task SaveGroup_UsesSelectedGroupAndRejectsCrossProjectGroup(int groupProjectId, bool allowed)
    {
        await using var site = await SplitSite.StartAsync();
        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.TaskGroups.Add(new TaskGroup { Id = 42, ProjectId = groupProjectId, CreatorId = 2, Name = "发布验收分组" });
            await db.SaveChangesAsync();
        }
        var form = site.SaveForm(await site.SplitAsync());
        AddRow(form, 0, "GROUPED_TASK");
        form["ProjectTaskGroups[1].Tasks[0].GroupId"] = "42";
        var response = await site.Client.PostAsync("/Tasks/SplitTask?handler=SaveAll", new FormUrlEncodedContent(form));
        using var check = site.Services.CreateScope();
        var saved = await check.ServiceProvider.GetRequiredService<ApplicationDbContext>().ToDoTasks.ToListAsync();
        if (allowed)
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(42, Assert.Single(saved).GroupId);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("任务分组不属于所选项目", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
            Assert.Empty(saved);
        }
    }

    internal static string Field(string html, string name)
    {
        var input = Regex.Matches(html, "<input[^>]*>").Select(m => m.Value)
            .First(v => v.Contains($"name=\"{name}\"", StringComparison.Ordinal));
        return WebUtility.HtmlDecode(Regex.Match(input, "value=\"([^\"]*)\"").Groups[1].Value);
    }
    private static string[] ReadTitles(string html) => Regex.Matches(html, "<input[^>]*>")
        .Select(m => m.Value).Where(v => v.Contains("task-title"))
        .Select(v => WebUtility.HtmlDecode(Regex.Match(v, "value=\"([^\"]*)\"").Groups[1].Value)).ToArray();

    private static void AddRow(Dictionary<string, string> form, int index, string title, bool deleted = false)
    {
        var prefix = $"ProjectTaskGroups[1].Tasks[{index}]";
        foreach (var pair in new Dictionary<string, string> { ["Title"] = title, ["Description"] = "review", ["ProjectId"] = "1", ["Priority"] = "Medium", ["Deadline"] = "", ["GroupId"] = "", ["IsDeleted"] = deleted ? "true" : "false" })
            form[$"{prefix}.{pair.Key}"] = pair.Value;
    }

    private sealed class SplitSite(WebApplication app, HttpClient client, SplitAI ai) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public IServiceProvider Services => app.Services;
        public SplitAI AI => ai;
        private string _token = "";
        public static async Task<SplitSite> StartAsync()
        {
            var ai = DispatchProxy.Create<IAIService, SplitAI>();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [], ApplicationName = typeof(SplitTaskModel).Assembly.FullName,
                EnvironmentName = "Development", ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            var databaseName = "split-page-" + Guid.NewGuid();
            builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(databaseName));
            builder.Services.AddIdentity<ApplicationUser, IdentityRole<int>>().AddEntityFrameworkStores<ApplicationDbContext>();
            builder.Services.AddRazorPages().AddApplicationPart(typeof(SplitTaskModel).Assembly);
            builder.Services.AddSingleton(ai);
            builder.Services.AddScoped(sp => new ToDoTaskDomainService(sp.GetRequiredService<ApplicationDbContext>(), sp.GetRequiredService<UserManager<ApplicationUser>>(), NullLogger<ProjectDomain>.Instance));
            var app = builder.Build();
            app.UseRouting();
            app.UseAuthentication();
            app.Use((context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "2")], "test"));
                return next(context);
            });
            app.UseAuthorization();
            app.MapRazorPages();
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.Users.Add(new ApplicationUser { Id = 2, UserName = "member", NormalizedUserName = "MEMBER", Role = UserRole.teamMember });
                db.Project.Add(new Project { Id = 1, Name = "Test", LeaderUserId = 2, CreatedByUserId = 2, Status = ProjectStatus.Active });
                await db.SaveChangesAsync();
            }
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            return new SplitSite(app, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) }, (SplitAI)(object)ai);
        }
        public async Task<string> SplitAsync()
        {
            var html = await Client.GetStringAsync("/Tasks/SplitTask?projectId=0");
            _token = Field(html, "__RequestVerificationToken");
            var response = await Client.PostAsync("/Tasks/SplitTask?handler=Split", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["ProjectId"] = "0", ["InputText"] = "拆分三个任务", ["__RequestVerificationToken"] = _token }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }
        public Dictionary<string, string> SaveForm(string html) => new()
        { ["ProjectId"] = "0", ["SubmissionId"] = Field(html, "SubmissionId"), ["__RequestVerificationToken"] = _token };
        public async Task<int> CountTasksAsync()
        {
            using var scope = app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ToDoTasks.CountAsync();
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }

    public class SplitAI : DispatchProxy
    {
        public int Calls { get; private set; }
        public string Output { get; set; } = "[{\"title\":\"FIRST\",\"projectId\":1},{\"title\":\"SECOND\",\"projectId\":1},{\"title\":\"THIRD\",\"projectId\":1}]";
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(IAIService.GetChatCompletionAsync)) throw new NotSupportedException();
            Calls++;
            return Task.FromResult(Output);
        }
    }
}

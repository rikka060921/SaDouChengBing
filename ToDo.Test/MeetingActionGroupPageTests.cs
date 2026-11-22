using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.AI;
using ToDo.Entities;
using DetailsModel = ToDo.Razor.Pages.MeetingMinutes.DetailsModel;

namespace ToDo.Test;

public class MeetingActionGroupPageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inline_ExplicitEmptyGroupClearsActionAndConfirmedTask(bool modelBoundToNull)
    {
        await using var f = await Fixture.CreateAsync();
        var page = f.Page();
        page.Request.Form = new FormCollection(new Dictionary<string, StringValues> { ["groupName"] = "" });
        var result = await page.OnPostUpdateActionItemFieldAsync(1, 1, groupName: modelBoundToNull ? null : "");
        Assert.True(Json(result).GetProperty("success").GetBoolean());
        f.Context.ChangeTracker.Clear();
        Assert.Null((await f.Context.MeetingActionItems.SingleAsync()).GroupName);
        Assert.Null((await f.Context.ToDoTasks.SingleAsync()).GroupId);
    }

    [Fact]
    public async Task Inline_OmittedGroupDoesNotClearExistingGroup()
    {
        await using var f = await Fixture.CreateAsync();
        var result = await f.Page().OnPostUpdateActionItemFieldAsync(1, 1, priority: "High");
        Assert.True(Json(result).GetProperty("success").GetBoolean());
        f.Context.ChangeTracker.Clear();
        Assert.Equal("研发", (await f.Context.MeetingActionItems.SingleAsync()).GroupName);
        Assert.Equal(1, (await f.Context.ToDoTasks.SingleAsync()).GroupId);
    }

    [Theory]
    [InlineData("跨项目分组")]
    [InlineData("已删除分组")]
    public async Task Inline_RejectsForeignOrDeletedGroup(string name)
    {
        await using var f = await Fixture.CreateAsync();
        var result = await f.Page().OnPostUpdateActionItemFieldAsync(1, 1, groupName: name);
        Assert.False(Json(result).GetProperty("success").GetBoolean());
        f.Context.ChangeTracker.Clear();
        Assert.Equal("研发", (await f.Context.MeetingActionItems.SingleAsync()).GroupName);
        Assert.Equal(1, (await f.Context.ToDoTasks.SingleAsync()).GroupId);
    }

    [Fact]
    public async Task Inline_AcceptsValidGroupWithSurroundingWhitespace()
    {
        await using var f = await Fixture.CreateAsync();
        var result = await f.Page().OnPostUpdateActionItemFieldAsync(1, 1, groupName: "  研发  ");
        Assert.True(Json(result).GetProperty("success").GetBoolean());
        Assert.Equal("研发", (await f.Context.MeetingActionItems.SingleAsync()).GroupName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Modal_AllowsNoGroupAndClearsConfirmedTask(string? group)
    {
        await using var f = await Fixture.CreateAsync();
        var page = f.Page();
        await page.OnPostUpdateActionItemAsync(1, 1, "修改后的任务", null, null, null, "Medium", "NotStarted", group);
        Assert.Null(page.TempData["ErrorMessage"]);
        Assert.NotNull(page.TempData["SuccessMessage"]);
        f.Context.ChangeTracker.Clear();
        Assert.Null((await f.Context.MeetingActionItems.SingleAsync()).GroupName);
        Assert.Null((await f.Context.ToDoTasks.SingleAsync()).GroupId);
        Assert.Equal("修改后的任务", (await f.Context.ToDoTasks.SingleAsync()).Title);
    }

    [Fact]
    public async Task Modal_RejectsForeignGroupWithoutPersistingEdits()
    {
        await using var f = await Fixture.CreateAsync();
        var page = f.Page();
        await page.OnPostUpdateActionItemAsync(1, 1, "不能保存", null, null, null, "Medium", "NotStarted", "跨项目分组");
        Assert.Equal("任务分组不属于当前项目", page.TempData["ErrorMessage"]);
        f.Context.ChangeTracker.Clear();
        Assert.Equal("研发", (await f.Context.MeetingActionItems.SingleAsync()).GroupName);
        Assert.Equal("原任务", (await f.Context.ToDoTasks.SingleAsync()).Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectSwitch_ClearsInvalidOldGroupAndExplainsResult(bool modal)
    {
        await using var f = await Fixture.CreateAsync();
        var item = await f.Context.MeetingActionItems.SingleAsync();
        item.IsConfirmed = false;
        item.MatchedTaskId = null;
        await f.Context.SaveChangesAsync();
        var page = f.Page();
        if (modal)
        {
            await page.OnPostUpdateActionItemAsync(1, 1, "切换项目", null, null, null, "Medium", "NotStarted", "研发", projectId: 20);
            Assert.Null(page.TempData["ErrorMessage"]);
            Assert.Contains("已改为无分组", page.TempData["SuccessMessage"]?.ToString());
        }
        else
        {
            var json = Json(await page.OnPostUpdateActionItemProjectAsync(1, 1, 20));
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.True(json.GetProperty("groupCleared").GetBoolean());
            Assert.Contains("已改为无分组", json.GetProperty("msg").GetString());
        }
        f.Context.ChangeTracker.Clear();
        item = await f.Context.MeetingActionItems.SingleAsync();
        Assert.Equal(20, item.ProjectId);
        Assert.Null(item.GroupName);
        Assert.Equal(1, (await f.Context.ToDoTasks.SingleAsync()).GroupId);
    }

    [Fact]
    public async Task GroupUpdate_StillRequiresMeetingWritePermission()
    {
        await using var f = await Fixture.CreateAsync();
        var result = Assert.IsType<JsonResult>(await f.Page(2).OnPostUpdateActionItemFieldAsync(1, 1, groupName: ""));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("研发", (await f.Context.MeetingActionItems.SingleAsync()).GroupName);
    }

    private static JsonElement Json(IActionResult result) => JsonSerializer.SerializeToElement(Assert.IsType<JsonResult>(result).Value);

    public class NoExternalAi : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("分组回归测试不允许调用 AI");
    }

    private sealed class EmptyTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private ServiceProvider provider = null!;
        public ApplicationDbContext Context = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            await f.connection.OpenAsync();
            f.Context = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(f.connection).Options);
            await f.Context.Database.EnsureCreatedAsync();
            f.Context.Users.AddRange(new ApplicationUser { Id = 1, UserName = "admin", Role = UserRole.systemAdmin, Status = UserStatus.Active },
                new ApplicationUser { Id = 2, UserName = "outsider", Role = UserRole.teamMember, Status = UserStatus.Active });
            await f.Context.SaveChangesAsync();
            f.Context.Project.AddRange(new Project { Id = 10, Name = "项目甲", CreatedByUserId = 1, LeaderUserId = 1 },
                new Project { Id = 20, Name = "项目乙", CreatedByUserId = 1, LeaderUserId = 1 });
            f.Context.TaskGroups.AddRange(new TaskGroup { Id = 1, Name = "研发", ProjectId = 10, CreatorId = 1 },
                new TaskGroup { Id = 2, Name = "跨项目分组", ProjectId = 20, CreatorId = 1 },
                new TaskGroup { Id = 3, Name = "已删除分组", ProjectId = 10, CreatorId = 1, IsDeleted = true });
            f.Context.MeetingMinutes.Add(new MeetingMinutes { Id = 1, ProjectId = 10, CreatorId = 1, MeetingTitle = "测试会议", MeetingContent = "测试", MeetingDate = AppTime.Today });
            await f.Context.SaveChangesAsync();
            f.Context.MeetingMinutesProjects.AddRange(new MeetingMinutesProject { MeetingMinutesId = 1, ProjectId = 10, IsPrimary = true },
                new MeetingMinutesProject { MeetingMinutesId = 1, ProjectId = 20 });
            f.Context.ToDoTasks.Add(new ToDoTask { Id = 1, CreatorId = 1, ProjectId = 10, Title = "原任务", GroupId = 1 });
            f.Context.MeetingActionItems.Add(new MeetingActionItem { Id = 1, MeetingMinutesId = 1, ProjectId = 10, Title = "原任务", GroupName = "研发", IsConfirmed = true, MatchedTaskId = 1 });
            await f.Context.SaveChangesAsync();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(f.Context);
            services.AddIdentity<ApplicationUser, IdentityRole<int>>().AddEntityFrameworkStores<ApplicationDbContext>();
            f.provider = services.BuildServiceProvider();
            return f;
        }
        public DetailsModel Page(int userId = 1)
        {
            var users = provider.GetRequiredService<UserManager<ApplicationUser>>();
            var ai = DispatchProxy.Create<IAIService, NoExternalAi>();
            var tasks = new ToDoTaskDomainService(Context, users, NullLogger<ProjectDomain>.Instance);
            var sync = new MeetingTaskSyncService(Context, ai, new UserNotificationService(Context), tasks, null!);
            var page = new DetailsModel(new MeetingMinutesService(Context, users, ai, null!), users, sync, null!, Context, tasks, ai, NullLogger<DetailsModel>.Instance);
            var http = new DefaultHttpContext { RequestServices = provider,
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")) };
            page.PageContext = new PageContext { HttpContext = http };
            page.TempData = new TempDataDictionary(http, new EmptyTempData());
            return page;
        }
        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Test;

public class ReportWorkflowRegressionTests
{
    [Theory]
    [InlineData(-2, 6, 2, 1)]
    [InlineData(99, 6, 2, 3)]
    [InlineData(99, 0, 10, 1)]
    [InlineData(int.MaxValue, 6, 2, 3)]
    [InlineData(2, 6, 2, 2)]
    public void Pagination_ClampsBeforeQuery(int page, int count, int size, int expected)
        => Assert.Equal(expected, DailyReportService.NormalizePage(page, count, size));

    [Theory]
    [InlineData(99, 2, 1)]
    [InlineData(-1, 6, 5)]
    [InlineData(2, 4, 3)]
    public async Task Pagination_ReturnsActualClampedPageAndStableOrder(int requested, int first, int second)
    {
        await using var db = await Fixture.CreateAsync();
        var service = new DailyReportService(db.Context, null!);
        var result = await service.GetFilteredDailyReports(null, null, "", "", null, null, requested, 2, db.Admin, true);
        Assert.Equal(6, result.TotalCount);
        Assert.Equal(new[] { first, second }, result.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task ArchivedHistory_RemainsAccessibleButCannotBeNewReportTarget()
    {
        await using var db = await Fixture.CreateAsync();
        var projects = MemberReportAccess.Projects(db.Context, db.Manager);
        Assert.Equal(10, (await projects.SingleAsync()).Id);
        Assert.Empty(await MemberReportAccess.Projects(db.Context, db.Manager, activeOnly: true).ToListAsync());
        Assert.Empty(await MemberReportAccess.Projects(db.Context, db.Member).ToListAsync());
        var service = new DailyReportService(db.Context, null!);
        var managed = await service.GetFilteredDailyReports(10, 4, "", "", null, null, 1, 10, db.Manager);
        Assert.Single(managed.Items);
        Assert.False((await service.GetCanDeletePermissions(managed.Items, db.Manager))[managed.Items[0].Id]);
        var deletion = await service.DeleteDailyReport(managed.Items[0].Id, db.Manager);
        Assert.False(deletion.Success);
        Assert.Equal(ProjectLifecycleRules.ReadOnlyMessage, deletion.Message);
        Assert.False((await db.Context.DailyReport.FindAsync(managed.Items[0].Id))!.IsDeleted);
        Assert.Empty((await service.GetFilteredDailyReports(10, 4, "", "", null, null, 1, 10, db.Member)).Items);
        Assert.False((await service.CheckReportPermission(db.Manager, await projects.SingleAsync())).HasPermission);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public ApplicationDbContext Context { get; }
        public ApplicationUser Admin { get; } = new() { Id = 1, UserName = "admin", Role = UserRole.systemAdmin };
        public ApplicationUser Manager { get; } = new() { Id = 2, UserName = "manager", Role = UserRole.teamMember };
        public ApplicationUser Member { get; } = new() { Id = 3, UserName = "member", Role = UserRole.teamMember };
        private Fixture(SqliteConnection connection, ApplicationDbContext context) { this.connection = connection; Context = context; }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var db = new Fixture(connection, context);
            context.AddRange(db.Admin, db.Manager, db.Member);
            await context.SaveChangesAsync();
            var project = new Project { Id = 10, Name = "历史项目", CreatedByUserId = 1, LeaderUserId = 1 };
            context.Add(project);
            await context.SaveChangesAsync();
            context.ProjectUsers.AddRange(new ProjectUser { ProjectId = 10, UserId = 2, ProjectRole = (int)ProjectRole.Admin },
                new ProjectUser { ProjectId = 10, UserId = 3, ProjectRole = (int)ProjectRole.Member });
            for (var i = 1; i <= 7; i++)
                context.DailyReport.Add(new DailyReport { Id = i, ProjectId = 10, ReporterId = 1,
                    ReportType = i == 7 ? 4 : 1, ReportDate = AppTime.Today, ReportTitle = "报告" + i, ReportContent = "历史内容" });
            await context.SaveChangesAsync();
            project.Status = ProjectStatus.Archived;
            await context.SaveChangesAsync();
            return db;
        }
        public async ValueTask DisposeAsync() { await Context.DisposeAsync(); await connection.DisposeAsync(); }
    }
}

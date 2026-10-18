using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Test;

public class MeetingMarkdownAttachmentTests
{
    [Theory]
    [InlineData("会议纪要.md", false)]
    [InlineData("会议纪要.MD", false)]
    [InlineData("会议纪要.md", true)]
    [InlineData("会议纪要.MD", true)]
    public async Task Markdown_CreateAndEdit_PreserveTextAndUseCanonicalMime(string name, bool editing)
    {
        await using var fixture = await Fixture.CreateAsync();
        var bytes = Encoding.UTF8.GetBytes("# 会议纪要\n\n- 决议：补充 Markdown 附件支持。\n");
        var file = File(name, bytes);
        // Never trust the MIME type supplied by a browser.
        file.ContentType = "text/html";
        if (editing)
        {
            await fixture.Service.SaveMeetingMinutes(fixture.Meeting, [], fixture.WebRoot, fixture.Admin);
            await fixture.Service.AddNewAttachments([file], fixture.Meeting, fixture.WebRoot, fixture.Admin);
        }
        else
        {
            await fixture.Service.SaveMeetingMinutes(fixture.Meeting, [file], fixture.WebRoot, fixture.Admin);
        }

        var attachment = await fixture.Context.MeetingAttachments.SingleAsync();
        Assert.Equal(name, attachment.FileName);
        Assert.Equal("text/markdown", attachment.ContentType);
        Assert.Equal(bytes.Length, attachment.FileSize);
        Assert.EndsWith(".md", attachment.FilePath);
        Assert.Equal(bytes, await System.IO.File.ReadAllBytesAsync(Path.Combine(fixture.WebRoot, attachment.FilePath)));
        Assert.Single(await fixture.Context.MeetingMinutes.ToListAsync());
    }

    [Theory]
    [InlineData("纪要.md.exe", 10, 1, false)]
    [InlineData("纪要.md", 0, 1, false)]
    [InlineData("纪要.md", 10485761, 1, false)]
    [InlineData("纪要.md", 10, 6, false)]
    [InlineData("纪要.md.exe", 10, 1, true)]
    [InlineData("纪要.md", 0, 1, true)]
    [InlineData("纪要.md", 10485761, 1, true)]
    [InlineData("纪要.md", 10, 6, true)]
    public async Task Markdown_DoesNotBypassExistingAttachmentLimits(string name, int size, int count, bool editing)
    {
        await using var fixture = await Fixture.CreateAsync();
        var files = Enumerable.Range(0, count).Select(_ => (IFormFile)File(name, new byte[size])).ToList();
        if (editing)
        {
            await fixture.Service.SaveMeetingMinutes(fixture.Meeting, [], fixture.WebRoot, fixture.Admin);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Service.AddNewAttachments(files, fixture.Meeting, fixture.WebRoot, fixture.Admin));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Service.SaveMeetingMinutes(fixture.Meeting, files, fixture.WebRoot, fixture.Admin));
        }
        Assert.Empty(await fixture.Context.MeetingAttachments.ToListAsync());
        Assert.False(Directory.Exists(fixture.WebRoot));
    }

    private static FormFile File(string name, byte[] bytes) => new(new MemoryStream(bytes), 0, bytes.Length, "Attachments", name)
    {
        Headers = new HeaderDictionary(), ContentType = "text/markdown"
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public ApplicationDbContext Context { get; }
        public MeetingMinutesService Service { get; }
        public ApplicationUser Admin { get; } = new() { Id = 1, UserName = "markdown-test", Role = UserRole.systemAdmin };
        public MeetingMinutes Meeting { get; } = new()
        {
            ProjectId = 10, CreatorId = 1, MeetingTitle = "Markdown 附件回归", MeetingContent = "会议内容",
            MeetingDate = AppTime.Today, IsDraft = true
        };
        public string WebRoot { get; } = Path.Combine(Path.GetTempPath(), "ToDo-Markdown-Attachments", Guid.NewGuid().ToString("N"));
        private Fixture(SqliteConnection connection, ApplicationDbContext context)
        {
            this.connection = connection;
            Context = context;
            Service = new MeetingMinutesService(context, null!, null!, null!);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var fixture = new Fixture(connection, context);
            context.Add(fixture.Admin);
            await context.SaveChangesAsync();
            context.Add(new Project { Id = 10, Name = "Markdown 测试项目", CreatedByUserId = 1, LeaderUserId = 1 });
            await context.SaveChangesAsync();
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            // Remove only files recorded by this isolated fixture; never recursively delete a computed directory.
            foreach (var attachment in await Context.MeetingAttachments.AsNoTracking().ToListAsync())
            {
                var path = Path.GetFullPath(Path.Combine(WebRoot, attachment.FilePath));
                if (!path.StartsWith(Path.GetFullPath(WebRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Test attachment escaped fixture directory");
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

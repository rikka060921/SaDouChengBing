using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Test;

public sealed class ProjectDocumentIndexServiceTests
{
    [Fact]
    public async Task ClaimAndProcess_TextDocument_CreatesPersistentChunks()
    {
        await using var database = await TestDatabase.CreateAsync("项目背景\n\n这是第一段。\n\n" + new string('甲', 2200), "background.md");

        var claimedId = await database.Index.ClaimNextAsync();
        Assert.Equal(database.Document.Id, claimedId);
        await database.Index.ProcessAsync(claimedId!.Value);

        await database.Context.Entry(database.Document).ReloadAsync();
        var chunks = await database.Context.ProjectDocumentChunks.AsNoTracking()
            .Where(chunk => chunk.ProjectDocumentId == database.Document.Id)
            .OrderBy(chunk => chunk.ChunkIndex)
            .ToListAsync();
        Assert.Equal(ProjectDocumentIndexStatus.Ready, database.Document.IndexStatus);
        Assert.True(database.Document.ExtractedCharacterCount > 2000);
        Assert.True(chunks.Count >= 2);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(chunk => chunk.ChunkIndex));
    }

    [Fact]
    public async Task ReadDocuments_QueryReturnsRelevantChunkWithinPromptBudget()
    {
        var content = new string('甲', 2100) + "\n\n火星基地关键方案：使用地下水制氧。\n\n" + new string('乙', 2100);
        await using var database = await TestDatabase.CreateAsync(content, "plan.txt");
        var claimedId = await database.Index.ClaimNextAsync();
        await database.Index.ProcessAsync(claimedId!.Value);
        database.Context.AgentDocumentPermissions.Add(new AgentDocumentPermission
        {
            ProjectId = database.Document.ProjectId,
            AgentKey = database.Agent.AgentKey,
            Category = ProjectDocumentCategory.Other,
            CanRead = true,
            UpdatedById = database.Admin.Id
        });
        await database.Context.SaveChangesAsync();
        var access = new AgentDocumentAccessService(database.Context, database.Environment);

        var documents = await access.ReadDocumentsAsync(
            database.Document.ProjectId,
            database.Agent.AgentKey,
            database.Admin.Id,
            maxCharactersPerDocument: 2000,
            query: "火星基地如何制氧",
            totalCharacterBudget: 2000);

        var readable = Assert.Single(documents);
        Assert.Contains("地下水制氧", readable.ContentExcerpt);
        Assert.True(readable.ContentExcerpt.Length <= 2000);
        Assert.Equal(ProjectDocumentIndexStatus.Ready, readable.IndexStatus);
    }

    [Fact]
    public async Task Process_UnsupportedFile_RecordsActionableStatusAndCanRequeue()
    {
        await using var database = await TestDatabase.CreateAsync("binary", "archive.bin");
        var claimedId = await database.Index.ClaimNextAsync();

        await database.Index.ProcessAsync(claimedId!.Value);
        await database.Context.Entry(database.Document).ReloadAsync();

        Assert.Equal(ProjectDocumentIndexStatus.Unsupported, database.Document.IndexStatus);
        Assert.Contains("暂不支持", database.Document.IndexError);
        await database.Index.ReindexAsync(database.Document.Id);
        Assert.Equal(ProjectDocumentIndexStatus.Pending, database.Document.IndexStatus);
        Assert.Equal(0, database.Document.IndexAttemptCount);
    }

    [Fact]
    public async Task ResolveStoragePath_RejectsTraversalOutsideStorageRoot()
    {
        await using var database = await TestDatabase.CreateAsync("safe", "safe.txt");

        Assert.Throws<InvalidOperationException>(() =>
            database.Documents.ResolveStoragePath("App_Data/ProjectDocuments/../../secret.txt"));
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _root;

        private TestDatabase(
            SqliteConnection connection,
            string root,
            ApplicationDbContext context,
            TestHostEnvironment environment,
            ProjectDocumentService documents,
            ProjectDocumentIndexService index,
            ProjectDocument document,
            ApplicationUser admin,
            AgentDefinition agent)
        {
            _connection = connection;
            _root = root;
            Context = context;
            Environment = environment;
            Documents = documents;
            Index = index;
            Document = document;
            Admin = admin;
            Agent = agent;
        }

        public ApplicationDbContext Context { get; }
        public TestHostEnvironment Environment { get; }
        public ProjectDocumentService Documents { get; }
        public ProjectDocumentIndexService Index { get; }
        public ProjectDocument Document { get; }
        public ApplicationUser Admin { get; }
        public AgentDefinition Agent { get; }

        public static async Task<TestDatabase> CreateAsync(string content, string fileName)
        {
            var root = Path.Combine(Path.GetTempPath(), "todo-document-index-tests", Guid.NewGuid().ToString("N"));
            var environment = new TestHostEnvironment { ContentRootPath = root };
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var admin = new ApplicationUser
            {
                UserName = "index-admin",
                RealName = "索引管理员",
                Role = UserRole.systemAdmin,
                Status = UserStatus.Active
            };
            var agent = new AgentDefinition
            {
                AgentKey = "knowledge-agent",
                Name = "知识 Agent",
                Description = "测试",
                SystemPrompt = "测试"
            };
            context.AddRange(admin, agent);
            await context.SaveChangesAsync();

            var projectId = 9001;
            context.Project.Add(new Project
            {
                Id = projectId,
                Name = "知识索引测试项目",
                CreatedByUserId = admin.Id,
                LeaderUserId = admin.Id,
                CreatedByUser = admin,
                LeaderUser = admin
            });
            await context.SaveChangesAsync();
            var storageName = $"{Guid.NewGuid():N}_{fileName}";
            var relativePath = Path.Combine("App_Data", "ProjectDocuments", projectId.ToString(), storageName)
                .Replace('\\', '/');
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content);
            var document = new ProjectDocument
            {
                ProjectId = projectId,
                UploadedById = admin.Id,
                FileName = fileName,
                StoragePath = relativePath,
                FileSize = new FileInfo(fullPath).Length,
                Category = ProjectDocumentCategory.Other,
                IsCurrent = true
            };
            context.ProjectDocuments.Add(document);
            await context.SaveChangesAsync();
            var documents = new ProjectDocumentService(context, environment);
            var index = new ProjectDocumentIndexService(
                context,
                documents,
                NullLogger<ProjectDocumentIndexService>.Instance);
            return new TestDatabase(connection, root, context, environment, documents, index, document, admin, agent);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    public sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ToDo.Test";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Test;

public class AgentDocumentAccessServiceTests
{
    [Fact]
    public async Task CustomCategoryPermission_DoesNotGrantPresetOtherCategory()
    {
        await using var context = CreateContext();
        var data = await SeedAsync(context);
        var service = new AgentDocumentAccessService(context, new TestHostEnvironment());

        Assert.True(await service.CanReadAsync(data.ProjectId, data.AgentKey, ProjectDocumentCategory.Other, categoryId: data.CustomCategoryId));
        Assert.False(await service.CanReadAsync(data.ProjectId, data.AgentKey, ProjectDocumentCategory.Other));
    }

    [Fact]
    public async Task ReadDocuments_CustomPermissionReturnsOnlyExactCustomCategory()
    {
        await using var context = CreateContext();
        var data = await SeedAsync(context);
        context.ProjectDocuments.AddRange(
            NewDocument(data.ProjectId, "allowed.pdf", data.CustomCategoryId),
            NewDocument(data.ProjectId, "other-custom.pdf", data.OtherCustomCategoryId),
            NewDocument(data.ProjectId, "preset-other.pdf", null));
        await context.SaveChangesAsync();
        var service = new AgentDocumentAccessService(context, new TestHostEnvironment());

        var documents = await service.ReadDocumentsAsync(data.ProjectId, data.AgentKey, data.AdminUserId);

        var document = Assert.Single(documents);
        Assert.Equal("allowed.pdf", document.FileName);
    }

    [Fact]
    public async Task SetPermission_RejectsCustomCategoryFromAnotherProject()
    {
        await using var context = CreateContext();
        var data = await SeedAsync(context);
        var foreignCategory = new DocumentCategory { ProjectId = data.ProjectId + 1, Name = "其他项目分类" };
        context.DocumentCategories.Add(foreignCategory);
        await context.SaveChangesAsync();
        var service = new AgentDocumentAccessService(context, new TestHostEnvironment());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetPermissionAsync(
            data.ProjectId,
            data.AgentKey,
            ProjectDocumentCategory.Other,
            true,
            false,
            data.AdminUserId,
            categoryId: foreignCategory.Id));

        Assert.Contains("不属于当前项目", exception.Message);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options);
    }

    private static async Task<TestData> SeedAsync(ApplicationDbContext context)
    {
        var admin = new ApplicationUser
        {
            UserName = "agent-test-admin",
            RealName = "Agent测试管理员",
            Role = UserRole.systemAdmin,
            Status = UserStatus.Active
        };
        context.Users.Add(admin);
        var agent = new AgentDefinition
        {
            AgentKey = "document-test-agent",
            Name = "资料权限测试 Agent",
            Description = "测试",
            SystemPrompt = "测试"
        };
        context.AgentDefinitions.Add(agent);
        var custom = new DocumentCategory { ProjectId = 100, Name = "客户材料" };
        var otherCustom = new DocumentCategory { ProjectId = 100, Name = "合同材料" };
        context.DocumentCategories.AddRange(custom, otherCustom);
        await context.SaveChangesAsync();
        context.AgentDocumentPermissions.Add(new AgentDocumentPermission
        {
            ProjectId = 100,
            AgentKey = agent.AgentKey,
            Category = ProjectDocumentCategory.Other,
            CategoryId = custom.Id,
            CanRead = true,
            UpdatedById = admin.Id
        });
        await context.SaveChangesAsync();
        return new TestData(100, admin.Id, agent.AgentKey, custom.Id, otherCustom.Id);
    }

    private static ProjectDocument NewDocument(int projectId, string fileName, int? categoryId)
    {
        return new ProjectDocument
        {
            ProjectId = projectId,
            UploadedById = 1,
            FileName = fileName,
            StoragePath = $"uploads/{fileName}",
            Category = ProjectDocumentCategory.Other,
            CategoryId = categoryId,
            Description = fileName,
            IsCurrent = true
        };
    }

    private sealed record TestData(int ProjectId, int AdminUserId, string AgentKey, int CustomCategoryId, int OtherCustomCategoryId);

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ToDo.Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

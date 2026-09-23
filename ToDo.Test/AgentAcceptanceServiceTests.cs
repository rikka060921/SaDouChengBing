using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Test;

public sealed class AgentAcceptanceServiceTests
{
    [Fact]
    public async Task Complete_ParsesStructuredRecommendationForExactReceipt()
    {
        await using var fixture = await AcceptanceFixture.CreateAsync();
        const string response = "<acceptance-result>{\"verdict\":\"reject\",\"confidence\":0.91,\"summary\":\"缺少关键测试\",\"evidenceCoverage\":[\"任务状态\"],\"missingItems\":[\"集成测试\"],\"risks\":[\"回归风险\"],\"humanReviewQuestions\":[\"是否允许延期\"]}</acceptance-result>";

        await new AgentAcceptanceService(fixture.Context).CompleteAsync(fixture.AcceptanceSession.Id, response);
        await fixture.Context.SaveChangesAsync();

        var saved = await fixture.Context.AgentAcceptanceRecommendations.AsNoTracking().SingleAsync();
        Assert.Equal(fixture.Receipt.Id, saved.DeliveryReceiptId);
        Assert.Equal(AgentAcceptanceRecommendationStatus.Completed, saved.Status);
        Assert.Equal(AgentAcceptanceVerdict.RecommendReject, saved.Verdict);
        Assert.Equal(0.91, saved.Confidence, 2);
        Assert.Equal("structured", saved.ParseMode);
        Assert.Contains("集成测试", AgentAcceptanceService.ParseItems(saved.MissingItemsJson));
    }

    [Fact]
    public async Task Complete_FallsBackSafelyWhenModelDoesNotReturnJson()
    {
        await using var fixture = await AcceptanceFixture.CreateAsync();

        await new AgentAcceptanceService(fixture.Context)
            .CompleteAsync(fixture.AcceptanceSession.Id, "建议通过，但仍请人工确认风险。");
        await fixture.Context.SaveChangesAsync();

        var saved = await fixture.Context.AgentAcceptanceRecommendations.AsNoTracking().SingleAsync();
        Assert.Equal(AgentAcceptanceVerdict.RecommendAccept, saved.Verdict);
        Assert.Equal(0.35, saved.Confidence, 2);
        Assert.Equal("fallback", saved.ParseMode);
    }

    private sealed class AcceptanceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private AcceptanceFixture(SqliteConnection connection, ApplicationDbContext context, AiSession acceptanceSession, AgentDeliveryReceipt receipt)
        {
            _connection = connection;
            Context = context;
            AcceptanceSession = acceptanceSession;
            Receipt = receipt;
        }

        public ApplicationDbContext Context { get; }
        public AiSession AcceptanceSession { get; }
        public AgentDeliveryReceipt Receipt { get; }

        public static async Task<AcceptanceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var user = new ApplicationUser { UserName = "acceptance-user", NormalizedUserName = "ACCEPTANCE-USER", Status = UserStatus.Active };
            context.Users.Add(user);
            await context.SaveChangesAsync();
            var project = new Project { Name = "验收项目", CreatedByUserId = user.Id, LeaderUserId = user.Id };
            var agent = new AgentDefinition { AgentKey = "source-agent", Name = "交付 Agent", IsEnabled = true };
            context.AddRange(project, agent);
            await context.SaveChangesAsync();
            var task = new ToDoTask { Title = "验收任务", CreatorId = user.Id, ProjectId = project.Id };
            var sourceSession = new AiSession { AgentKey = agent.AgentKey, UserId = user.Id, ProjectId = project.Id };
            var acceptanceSession = new AiSession { AgentKey = "delivery-acceptance", UserId = user.Id, ProjectId = project.Id };
            context.AddRange(task, sourceSession, acceptanceSession);
            await context.SaveChangesAsync();
            acceptanceSession.TaskId = task.Id;
            var workItem = new AgentWorkItem
            {
                AgentDefinitionId = agent.Id,
                ProjectId = project.Id,
                TaskId = task.Id,
                RequestedByUserId = user.Id,
                AiSessionId = sourceSession.Id,
                IdempotencyKey = "acceptance-source",
                Status = AgentWorkItemStatus.Completed
            };
            context.AgentWorkItems.Add(workItem);
            await context.SaveChangesAsync();
            var receipt = new AgentDeliveryReceipt
            {
                AgentWorkItemId = workItem.Id,
                AgentDefinitionId = agent.Id,
                ProjectId = project.Id,
                TaskId = task.Id,
                AiSessionId = sourceSession.Id,
                ContentHash = new string('a', 64)
            };
            context.AgentDeliveryReceipts.Add(receipt);
            await context.SaveChangesAsync();
            acceptanceSession.ContextDeliveryReceiptId = receipt.Id;
            var job = new AgentRunJob
            {
                AiSessionId = acceptanceSession.Id,
                AgentDefinitionId = agent.Id,
                RequestedByUserId = user.Id,
                ProjectId = project.Id,
                TaskId = task.Id,
                DeliveryReceiptId = receipt.Id
            };
            context.AgentRunJobs.Add(job);
            await context.SaveChangesAsync();
            context.AgentAcceptanceRecommendations.Add(new AgentAcceptanceRecommendation
            {
                DeliveryReceiptId = receipt.Id,
                AiSessionId = acceptanceSession.Id,
                AgentRunJobId = job.Id,
                RequestedByUserId = user.Id
            });
            await context.SaveChangesAsync();
            return new AcceptanceFixture(connection, context, acceptanceSession, receipt);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}

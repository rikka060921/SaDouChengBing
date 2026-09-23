using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ToDo.Context;

namespace ToDo.Test;

public sealed class MigrationSafetyTests
{
    // 固定版本的 MySQL provider 仅用于离线生成 SQL/比较模型，不连接数据库。
    private static ApplicationDbContext MultiProjectContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseMySql("Server=localhost;Database=migration_safety;Uid=test;Pwd=test;",
            new MySqlServerVersion(new Version(8, 0, 30))).Options);

    [Fact]
    public void MultiProjectUpgrade_DoesNotIntroduceAnotherInitializationMigration()
    {
        using var context = MultiProjectContext();
        var migrations = context.Database.GetMigrations().ToList();
        Assert.Contains("20260826081313_InitDB", migrations);
        Assert.DoesNotContain("20260919102504_InitDB", migrations);
        Assert.Equal("20260919105130_AddMeetingMultiProjectSupport", migrations[^1]);
        Assert.Single(migrations, id => id.EndsWith("_InitDB", StringComparison.Ordinal));
    }

    [Fact]
    public void MultiProjectUpgrade_OnlyCreatesJoinTableAndAltersProjectColumns()
    {
        using var context = MultiProjectContext();
        var script = context.GetService<IMigrator>().GenerateScript(
            "20260918042404_AddPrepDraftTaskIdToActionItem", "20260919105130_AddMeetingMultiProjectSupport");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, "CREATE TABLE", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.Contains("CREATE TABLE `MeetingMinutesProjects`", script);
        Assert.Contains("ALTER TABLE `MeetingActionItems` ADD `ProjectId`", script);
        Assert.Contains("ALTER TABLE `meeting_action_supervision_events`", script);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP COLUMN", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MultiProjectSnapshot_MatchesCurrentModelAfterRemovingDuplicateBaseline()
    {
        using var context = MultiProjectContext();
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void TaskSplitSubmissionUpgrade_OnlyAddsIdempotencyTable()
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseMySql("Server=localhost;Database=migration_safety;Uid=test;Pwd=test;", new MySqlServerVersion(new Version(8, 0, 30))).Options);
        var script = context.GetService<IMigrator>().GenerateScript(
            "20260908130751_AddMeetingActionItemSourceDecision", "20260912135247_AddTaskSplitSubmissions");
        Assert.Contains("CREATE TABLE `TaskSplitSubmissions`", script);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("ALTER TABLE", script);
        Assert.DoesNotContain("DELETE FROM", script);
    }

    [Fact]
    public void UpgradeAfterAgentDispatching_DoesNotRecreateExistingTables()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseMySql(
                "Server=localhost;Database=migration_safety;Uid=test;Pwd=test;",
                new MySqlServerVersion(new Version(8, 0, 30)))
            .Options;
        using var context = new ApplicationDbContext(options);
        var migrator = context.GetService<IMigrator>();

        var script = migrator.GenerateScript(
            "20260823123702_AddAgentDispatching",
            "20260826105923_Add_MeetingActionItem_TaskChange_Fields");

        Assert.DoesNotContain("CREATE TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MeetingActionItems", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AfterStatus", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KnowledgeIndexUpgrade_IsIncrementalAndPreservesExistingDocuments()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseMySql(
                "Server=localhost;Database=migration_safety;Uid=test;Pwd=test;",
                new MySqlServerVersion(new Version(8, 0, 30)))
            .Options;
        using var context = new ApplicationDbContext(options);
        var migrator = context.GetService<IMigrator>();

        var script = migrator.GenerateScript(
            "20260826105923_Add_MeetingActionItem_TaskChange_Fields",
            "20260827033252_AddProjectDocumentKnowledgeIndex");

        Assert.DoesNotContain("DROP TABLE `project_documents`", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE `project_documents`", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ALTER TABLE `project_documents`", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE `project_document_chunks`", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IntegrationHardeningUpgrade_OnlyAltersOwnedTables()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseMySql(
                "Server=localhost;Database=migration_safety;Uid=test;Pwd=test;",
                new MySqlServerVersion(new Version(8, 0, 30)))
            .Options;
        using var context = new ApplicationDbContext(options);
        var migrator = context.GetService<IMigrator>();
        var script = migrator.GenerateScript(
            "20260827033252_AddProjectDocumentKnowledgeIndex",
            "20260827034733_HardenIntegrationsAndScheduledJobs");

        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ALTER TABLE `integration_call_records`", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ALTER TABLE `scheduled_jobs`", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecurityAndRetentionUpgrade_PreservesExistingEvidenceAndMarksLegacySignatures()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseMySql(
                "Server=localhost;Database=migration_safety;Uid=test;Pwd=test;",
                new MySqlServerVersion(new Version(8, 0, 30)))
            .Options;
        using var context = new ApplicationDbContext(options);
        var migrator = context.GetService<IMigrator>();

        var script = migrator.GenerateScript(
            "20260831160810_AgentManagedReleaseAndCanary",
            "20260831162731_SecurityObservabilityAndDataRetention");

        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM `agent_delivery_receipts`", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("legacy-hash-only", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE `data_retention_runs`", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ADD `ArchivedAt`", script, StringComparison.OrdinalIgnoreCase);
    }
}

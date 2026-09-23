using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AgentManagedReleaseAndCanary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AvailableManagedDefinitionVersion",
                table: "agent_definitions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CanaryPercent",
                table: "agent_definitions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CanaryVersion",
                table: "agent_definitions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeploymentStatus",
                table: "agent_definitions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "HasLocalOverrides",
                table: "agent_definitions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSystemManaged",
                table: "agent_definitions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ManagedDefinitionVersion",
                table: "agent_definitions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StableVersion",
                table: "agent_definitions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // 现有运行版本先原样成为稳定版本；内置 Agent 的历史自定义无法可靠区分，
            // 因此默认视为本地覆盖，只提示可升级而不在迁移时静默改写。
            migrationBuilder.Sql("UPDATE agent_definitions SET StableVersion = Version WHERE StableVersion = 0;");
            migrationBuilder.Sql("UPDATE agent_definitions SET IsSystemManaged = 1, ManagedDefinitionVersion = 1, AvailableManagedDefinitionVersion = 2, HasLocalOverrides = CASE WHEN SystemPrompt IS NULL OR TRIM(SystemPrompt) = '' THEN 0 ELSE 1 END WHERE TemplateKey = 'system-default';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvailableManagedDefinitionVersion",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "CanaryPercent",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "CanaryVersion",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "DeploymentStatus",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "HasLocalOverrides",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "IsSystemManaged",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "ManagedDefinitionVersion",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "StableVersion",
                table: "agent_definitions");
        }
    }
}

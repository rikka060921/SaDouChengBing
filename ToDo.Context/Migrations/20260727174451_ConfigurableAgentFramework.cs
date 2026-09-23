using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class ConfigurableAgentFramework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoCommentOnCompletion",
                table: "agent_definitions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ContextSourcesJson",
                table: "agent_definitions",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "[]")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "MaxTokens",
                table: "agent_definitions",
                type: "int",
                nullable: false,
                defaultValue: 6000);

            migrationBuilder.AddColumn<int>(
                name: "MaxTurns",
                table: "agent_definitions",
                type: "int",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.AddColumn<string>(
                name: "ModelName",
                table: "agent_definitions",
                type: "varchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "RequiresProject",
                table: "agent_definitions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresTask",
                table: "agent_definitions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SystemPrompt",
                table: "agent_definitions",
                type: "text",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<double>(
                name: "Temperature",
                table: "agent_definitions",
                type: "double",
                nullable: false,
                defaultValue: 0.3);

            migrationBuilder.AddColumn<int>(
                name: "TimeoutSeconds",
                table: "agent_definitions",
                type: "int",
                nullable: false,
                defaultValue: 90);

            migrationBuilder.CreateTable(
                name: "agent_tool_permissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentDefinitionId = table.Column<int>(type: "int", nullable: false),
                    ToolName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_tool_permissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_tool_permissions_agent_definitions_AgentDefinitionId",
                        column: x => x.AgentDefinitionId,
                        principalTable: "agent_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_agent_definitions_AgentKey",
                table: "agent_definitions",
                column: "AgentKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_tool_permissions_AgentDefinitionId_ToolName",
                table: "agent_tool_permissions",
                columns: new[] { "AgentDefinitionId", "ToolName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_tool_permissions");

            migrationBuilder.DropIndex(
                name: "IX_agent_definitions_AgentKey",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "AutoCommentOnCompletion",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "ContextSourcesJson",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "MaxTokens",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "MaxTurns",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "ModelName",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "RequiresProject",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "RequiresTask",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "SystemPrompt",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "Temperature",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "TimeoutSeconds",
                table: "agent_definitions");
        }
    }
}

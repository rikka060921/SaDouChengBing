using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AgentProductLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastTestAt",
                table: "agent_definitions",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastTestSessionId",
                table: "agent_definitions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastTestStatus",
                table: "agent_definitions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LifecycleStatus",
                table: "agent_definitions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "PublicationGatePassed",
                table: "agent_definitions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TemplateKey",
                table: "agent_definitions",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            // 兼容升级前的运行状态：原先启用的 Agent 继续保持已发布，原先停用的 Agent 进入暂停。
            // 这些配置属于迁移前已投入使用的存量版本，视为历史验收通过；后续任何编辑仍会撤销门禁并要求重测。
            migrationBuilder.Sql(
                "UPDATE `agent_definitions` " +
                "SET `LifecycleStatus` = CASE WHEN `IsEnabled` = 1 THEN 2 ELSE 3 END, " +
                "`PublicationGatePassed` = 1, `LastTestStatus` = 2, `TemplateKey` = 'legacy-import'");

            migrationBuilder.CreateTable(
                name: "agent_acceptance_contracts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentDefinitionId = table.Column<int>(type: "int", nullable: false),
                    Objective = table.Column<string>(type: "text", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InputRequirements = table.Column<string>(type: "text", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequiredOutput = table.Column<string>(type: "text", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SuccessCriteria = table.Column<string>(type: "text", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProhibitedActions = table.Column<string>(type: "text", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TestPrompt = table.Column<string>(type: "text", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ExpectedOutputTerms = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ForbiddenOutputTerms = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_acceptance_contracts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_acceptance_contracts_agent_definitions_AgentDefinition~",
                        column: x => x.AgentDefinitionId,
                        principalTable: "agent_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_test_runs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentDefinitionId = table.Column<int>(type: "int", nullable: false),
                    AgentVersion = table.Column<int>(type: "int", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    TaskId = table.Column<int>(type: "int", nullable: true),
                    AiSessionId = table.Column<int>(type: "int", nullable: true),
                    Prompt = table.Column<string>(type: "text", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ResultSummary = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ValidationSummary = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_test_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_test_runs_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_test_runs_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_test_runs_agent_definitions_AgentDefinitionId",
                        column: x => x.AgentDefinitionId,
                        principalTable: "agent_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agent_test_runs_ai_sessions_AiSessionId",
                        column: x => x.AiSessionId,
                        principalTable: "ai_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_test_runs_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_agent_acceptance_contracts_AgentDefinitionId",
                table: "agent_acceptance_contracts",
                column: "AgentDefinitionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_test_runs_AgentDefinitionId_StartedAt",
                table: "agent_test_runs",
                columns: new[] { "AgentDefinitionId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_test_runs_AiSessionId",
                table: "agent_test_runs",
                column: "AiSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_test_runs_ProjectId",
                table: "agent_test_runs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_test_runs_RequestedByUserId",
                table: "agent_test_runs",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_test_runs_TaskId",
                table: "agent_test_runs",
                column: "TaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_acceptance_contracts");

            migrationBuilder.DropTable(
                name: "agent_test_runs");

            migrationBuilder.DropColumn(
                name: "LastTestAt",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "LastTestSessionId",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "LastTestStatus",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "LifecycleStatus",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "PublicationGatePassed",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "TemplateKey",
                table: "agent_definitions");
        }
    }
}

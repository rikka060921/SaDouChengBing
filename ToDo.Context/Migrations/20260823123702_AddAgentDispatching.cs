using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentDispatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Project",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(1000)",
                oldMaxLength: 1000)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "CanReceiveTaskDispatch",
                table: "agent_definitions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE agent_definitions
                SET CanReceiveTaskDispatch = 1
                WHERE AgentKey NOT IN ('task-dispatcher', 'red-team', 'blue-team', 'judge', 'red-blue-host');
                """);

            migrationBuilder.CreateTable(
                name: "agent_dispatch_decisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: false),
                    DispatchVersion = table.Column<int>(type: "int", nullable: false),
                    RecommendedAgentDefinitionId = table.Column<int>(type: "int", nullable: true),
                    SelectedAgentDefinitionId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Confidence = table.Column<double>(type: "double", nullable: false),
                    CandidatesJson = table.Column<string>(type: "text", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Explanation = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_dispatch_decisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_dispatch_decisions_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_dispatch_decisions_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agent_dispatch_decisions_agent_definitions_RecommendedAgentD~",
                        column: x => x.RecommendedAgentDefinitionId,
                        principalTable: "agent_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_dispatch_decisions_agent_definitions_SelectedAgentDefi~",
                        column: x => x.SelectedAgentDefinitionId,
                        principalTable: "agent_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_dispatch_decisions_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_agent_dispatch_decisions_ProjectId",
                table: "agent_dispatch_decisions",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_dispatch_decisions_RecommendedAgentDefinitionId",
                table: "agent_dispatch_decisions",
                column: "RecommendedAgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_dispatch_decisions_RequestedByUserId",
                table: "agent_dispatch_decisions",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_dispatch_decisions_SelectedAgentDefinitionId",
                table: "agent_dispatch_decisions",
                column: "SelectedAgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_dispatch_decisions_TaskId_DispatchVersion",
                table: "agent_dispatch_decisions",
                columns: new[] { "TaskId", "DispatchVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_dispatch_decisions");

            migrationBuilder.DropColumn(
                name: "CanReceiveTaskDispatch",
                table: "agent_definitions");

            migrationBuilder.UpdateData(
                table: "Project",
                keyColumn: "Description",
                keyValue: null,
                column: "Description",
                value: "");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Project",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(1000)",
                oldMaxLength: 1000,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }
    }
}

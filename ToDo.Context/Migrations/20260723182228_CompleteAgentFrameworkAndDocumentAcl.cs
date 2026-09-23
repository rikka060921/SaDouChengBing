using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class CompleteAgentFrameworkAndDocumentAcl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentKey",
                table: "TaskComments",
                type: "varchar(80)",
                maxLength: 80,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "AiSessionId",
                table: "TaskComments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "project_documents",
                type: "int",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastActivityAt",
                table: "ai_sessions",
                type: "datetime(6)",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP(6)");

            migrationBuilder.Sql("UPDATE `ai_sessions` SET `LastActivityAt` = `StartedAt`;");

            migrationBuilder.AddColumn<int>(
                name: "TurnCount",
                table: "ai_sessions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "agent_document_access_logs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    DocumentId = table.Column<int>(type: "int", nullable: true),
                    AiSessionId = table.Column<int>(type: "int", nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    AgentKey = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Category = table.Column<int>(type: "int", nullable: false),
                    AccessType = table.Column<int>(type: "int", nullable: false),
                    IsAllowed = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Reason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_document_access_logs", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_document_permissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    AgentKey = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Category = table.Column<int>(type: "int", nullable: false),
                    CanRead = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CanWrite = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    UpdatedById = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_document_permissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_document_permissions_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_tool_calls",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AiSessionId = table.Column<int>(type: "int", nullable: false),
                    ToolName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ArgumentsJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ResultJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequiresApproval = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ApprovalRequestId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_tool_calls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_tool_calls_ai_sessions_AiSessionId",
                        column: x => x.AiSessionId,
                        principalTable: "ai_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agent_tool_calls_approval_requests_ApprovalRequestId",
                        column: x => x.ApprovalRequestId,
                        principalTable: "approval_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ai_session_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AiSessionId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ToolName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_session_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_session_messages_ai_sessions_AiSessionId",
                        column: x => x.AiSessionId,
                        principalTable: "ai_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_agent_document_access_logs_ProjectId_AgentKey_CreatedAt",
                table: "agent_document_access_logs",
                columns: new[] { "ProjectId", "AgentKey", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_document_permissions_ProjectId_AgentKey_Category",
                table: "agent_document_permissions",
                columns: new[] { "ProjectId", "AgentKey", "Category" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_tool_calls_AiSessionId",
                table: "agent_tool_calls",
                column: "AiSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_tool_calls_ApprovalRequestId",
                table: "agent_tool_calls",
                column: "ApprovalRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_session_messages_AiSessionId",
                table: "ai_session_messages",
                column: "AiSessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_document_access_logs");

            migrationBuilder.DropTable(
                name: "agent_document_permissions");

            migrationBuilder.DropTable(
                name: "agent_tool_calls");

            migrationBuilder.DropTable(
                name: "ai_session_messages");

            migrationBuilder.DropColumn(
                name: "AgentKey",
                table: "TaskComments");

            migrationBuilder.DropColumn(
                name: "AiSessionId",
                table: "TaskComments");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "project_documents");

            migrationBuilder.DropColumn(
                name: "LastActivityAt",
                table: "ai_sessions");

            migrationBuilder.DropColumn(
                name: "TurnCount",
                table: "ai_sessions");
        }
    }
}

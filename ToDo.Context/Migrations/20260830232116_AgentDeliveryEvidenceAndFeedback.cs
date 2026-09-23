using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AgentDeliveryEvidenceAndFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_delivery_receipts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentWorkItemId = table.Column<int>(type: "int", nullable: false),
                    AgentDefinitionId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    AiSessionId = table.Column<int>(type: "int", nullable: false),
                    AgentVersion = table.Column<int>(type: "int", nullable: false),
                    OutcomeSummary = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EvidenceJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ToolEffectsJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ValidationSummary = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RiskSummary = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ContentHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EvidenceQuality = table.Column<int>(type: "int", nullable: false),
                    AcceptanceStatus = table.Column<int>(type: "int", nullable: false),
                    ReviewedByUserId = table.Column<int>(type: "int", nullable: true),
                    ReviewComment = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_delivery_receipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_delivery_receipts_AspNetUsers_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_delivery_receipts_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_delivery_receipts_agent_definitions_AgentDefinitionId",
                        column: x => x.AgentDefinitionId,
                        principalTable: "agent_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_delivery_receipts_agent_work_items_AgentWorkItemId",
                        column: x => x.AgentWorkItemId,
                        principalTable: "agent_work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agent_delivery_receipts_ai_sessions_AiSessionId",
                        column: x => x.AiSessionId,
                        principalTable: "ai_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_delivery_receipts_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_performance_signals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentDefinitionId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    TaskId = table.Column<int>(type: "int", nullable: true),
                    AgentWorkItemId = table.Column<int>(type: "int", nullable: true),
                    DeliveryReceiptId = table.Column<long>(type: "bigint", nullable: true),
                    DispatchDecisionId = table.Column<int>(type: "int", nullable: true),
                    EventType = table.Column<int>(type: "int", nullable: false),
                    ScoreDelta = table.Column<double>(type: "double", nullable: false),
                    EventKey = table.Column<string>(type: "varchar(160)", maxLength: 160, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reason = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_performance_signals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_performance_signals_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_performance_signals_agent_definitions_AgentDefinitionId",
                        column: x => x.AgentDefinitionId,
                        principalTable: "agent_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_performance_signals_agent_delivery_receipts_DeliveryRe~",
                        column: x => x.DeliveryReceiptId,
                        principalTable: "agent_delivery_receipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_performance_signals_agent_dispatch_decisions_DispatchD~",
                        column: x => x.DispatchDecisionId,
                        principalTable: "agent_dispatch_decisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_performance_signals_agent_work_items_AgentWorkItemId",
                        column: x => x.AgentWorkItemId,
                        principalTable: "agent_work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_performance_signals_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_agent_delivery_receipts_AgentDefinitionId",
                table: "agent_delivery_receipts",
                column: "AgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_delivery_receipts_AgentWorkItemId",
                table: "agent_delivery_receipts",
                column: "AgentWorkItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_delivery_receipts_AiSessionId",
                table: "agent_delivery_receipts",
                column: "AiSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_delivery_receipts_ProjectId",
                table: "agent_delivery_receipts",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_delivery_receipts_ReviewedByUserId",
                table: "agent_delivery_receipts",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_delivery_receipts_TaskId_CreatedAt",
                table: "agent_delivery_receipts",
                columns: new[] { "TaskId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_performance_signals_AgentDefinitionId_CreatedAt",
                table: "agent_performance_signals",
                columns: new[] { "AgentDefinitionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_performance_signals_AgentWorkItemId",
                table: "agent_performance_signals",
                column: "AgentWorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_performance_signals_DeliveryReceiptId",
                table: "agent_performance_signals",
                column: "DeliveryReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_performance_signals_DispatchDecisionId",
                table: "agent_performance_signals",
                column: "DispatchDecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_performance_signals_EventKey",
                table: "agent_performance_signals",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_performance_signals_ProjectId",
                table: "agent_performance_signals",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_performance_signals_TaskId",
                table: "agent_performance_signals",
                column: "TaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_performance_signals");

            migrationBuilder.DropTable(
                name: "agent_delivery_receipts");
        }
    }
}

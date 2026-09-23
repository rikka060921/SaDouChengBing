using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class UnifiedActionAndStructuredAcceptance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NextCheckpointAt",
                table: "MeetingActionItems",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SnoozedUntil",
                table: "MeetingActionItems",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SupervisionAcknowledgedAt",
                table: "MeetingActionItems",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SupervisionAcknowledgedByUserId",
                table: "MeetingActionItems",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupervisionResolutionNote",
                table: "MeetingActionItems",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_acceptance_recommendations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    DeliveryReceiptId = table.Column<long>(type: "bigint", nullable: false),
                    AiSessionId = table.Column<int>(type: "int", nullable: false),
                    AgentRunJobId = table.Column<long>(type: "bigint", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Verdict = table.Column<int>(type: "int", nullable: false),
                    Confidence = table.Column<double>(type: "double", nullable: false),
                    Summary = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EvidenceCoverageJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MissingItemsJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RisksJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    HumanReviewQuestionsJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RawResponse = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErrorMessage = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ParseMode = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_acceptance_recommendations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_acceptance_recommendations_AspNetUsers_RequestedByUser~",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_acceptance_recommendations_agent_delivery_receipts_Del~",
                        column: x => x.DeliveryReceiptId,
                        principalTable: "agent_delivery_receipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_acceptance_recommendations_agent_run_jobs_AgentRunJobId",
                        column: x => x.AgentRunJobId,
                        principalTable: "agent_run_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_agent_acceptance_recommendations_AgentRunJobId",
                table: "agent_acceptance_recommendations",
                column: "AgentRunJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_acceptance_recommendations_AiSessionId",
                table: "agent_acceptance_recommendations",
                column: "AiSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_acceptance_recommendations_DeliveryReceiptId_CreatedAt",
                table: "agent_acceptance_recommendations",
                columns: new[] { "DeliveryReceiptId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_acceptance_recommendations_RequestedByUserId",
                table: "agent_acceptance_recommendations",
                column: "RequestedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_acceptance_recommendations");

            migrationBuilder.DropColumn(
                name: "NextCheckpointAt",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "SnoozedUntil",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "SupervisionAcknowledgedAt",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "SupervisionAcknowledgedByUserId",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "SupervisionResolutionNote",
                table: "MeetingActionItems");
        }
    }
}

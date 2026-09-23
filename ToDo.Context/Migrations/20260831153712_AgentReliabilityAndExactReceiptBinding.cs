using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AgentReliabilityAndExactReceiptBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ContextDeliveryReceiptId",
                table: "ai_sessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DeliveryReceiptId",
                table: "agent_run_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "background_job_leases",
                columns: table => new
                {
                    LeaseKey = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OwnerId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AcquiredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_background_job_leases", x => x.LeaseKey);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ai_sessions_ContextDeliveryReceiptId",
                table: "ai_sessions",
                column: "ContextDeliveryReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_run_jobs_DeliveryReceiptId",
                table: "agent_run_jobs",
                column: "DeliveryReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_background_job_leases_ExpiresAt",
                table: "background_job_leases",
                column: "ExpiresAt");

            migrationBuilder.AddForeignKey(
                name: "FK_agent_run_jobs_agent_delivery_receipts_DeliveryReceiptId",
                table: "agent_run_jobs",
                column: "DeliveryReceiptId",
                principalTable: "agent_delivery_receipts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_agent_run_jobs_agent_delivery_receipts_DeliveryReceiptId",
                table: "agent_run_jobs");

            migrationBuilder.DropTable(
                name: "background_job_leases");

            migrationBuilder.DropIndex(
                name: "IX_ai_sessions_ContextDeliveryReceiptId",
                table: "ai_sessions");

            migrationBuilder.DropIndex(
                name: "IX_agent_run_jobs_DeliveryReceiptId",
                table: "agent_run_jobs");

            migrationBuilder.DropColumn(
                name: "ContextDeliveryReceiptId",
                table: "ai_sessions");

            migrationBuilder.DropColumn(
                name: "DeliveryReceiptId",
                table: "agent_run_jobs");
        }
    }
}

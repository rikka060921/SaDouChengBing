using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class SecurityObservabilityAndDataRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PayloadSignature",
                table: "approval_requests",
                type: "varchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "SignatureKeyId",
                table: "approval_requests",
                type: "varchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "ai_sessions",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentSignature",
                table: "agent_delivery_receipts",
                type: "varchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "SignatureKeyId",
                table: "agent_delivery_receipts",
                type: "varchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            // 历史记录创建时只有公开哈希，没有服务端密钥签名。显式标记后仍可验收，
            // 同时不会被界面误报为使用当前密钥生成的签名。
            migrationBuilder.Sql("UPDATE `approval_requests` SET `SignatureKeyId` = 'legacy-hash-only' WHERE `PayloadSignature` = '';");
            migrationBuilder.Sql("UPDATE `agent_delivery_receipts` SET `SignatureKeyId` = 'legacy-hash-only' WHERE `ContentSignature` = '';");

            migrationBuilder.CreateTable(
                name: "data_retention_runs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    TriggeredByUserId = table.Column<int>(type: "int", nullable: true),
                    Trigger = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ArchiveSessionsBefore = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    DeleteNotificationsBefore = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ArchivedSessionCount = table.Column<int>(type: "int", nullable: false),
                    DeletedNotificationCount = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StartedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_retention_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_data_retention_runs_AspNetUsers_TriggeredByUserId",
                        column: x => x.TriggeredByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ai_sessions_ArchivedAt_LastActivityAt",
                table: "ai_sessions",
                columns: new[] { "ArchivedAt", "LastActivityAt" });

            migrationBuilder.CreateIndex(
                name: "IX_data_retention_runs_StartedAt",
                table: "data_retention_runs",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_data_retention_runs_TriggeredByUserId",
                table: "data_retention_runs",
                column: "TriggeredByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_retention_runs");

            migrationBuilder.DropIndex(
                name: "IX_ai_sessions_ArchivedAt_LastActivityAt",
                table: "ai_sessions");

            migrationBuilder.DropColumn(
                name: "PayloadSignature",
                table: "approval_requests");

            migrationBuilder.DropColumn(
                name: "SignatureKeyId",
                table: "approval_requests");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "ai_sessions");

            migrationBuilder.DropColumn(
                name: "ContentSignature",
                table: "agent_delivery_receipts");

            migrationBuilder.DropColumn(
                name: "SignatureKeyId",
                table: "agent_delivery_receipts");
        }
    }
}

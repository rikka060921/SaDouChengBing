using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalDailySummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSystemGenerated",
                table: "DailyReport",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE `DailyReport` SET `IsSystemGenerated` = 1 WHERE `ReportTitle` LIKE '%自动日报%';");

            migrationBuilder.AddColumn<bool>(
                name: "IsAutomatic",
                table: "daily_work_summary",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "SummaryEndAt",
                table: "daily_work_summary",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SummaryStartAt",
                table: "daily_work_summary",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "daily_work_summary",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_summary_checkpoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    LastSuccessfulSummaryAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastDailyWorkSummaryId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_summary_checkpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_summary_checkpoints_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_summary_checkpoints_daily_work_summary_LastDailyWorkSum~",
                        column: x => x.LastDailyWorkSummaryId,
                        principalTable: "daily_work_summary",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_daily_work_summary_UserId_SummaryDate",
                table: "daily_work_summary",
                columns: new[] { "UserId", "SummaryDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_summary_checkpoints_LastDailyWorkSummaryId",
                table: "user_summary_checkpoints",
                column: "LastDailyWorkSummaryId");

            migrationBuilder.CreateIndex(
                name: "IX_user_summary_checkpoints_UserId",
                table: "user_summary_checkpoints",
                column: "UserId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_daily_work_summary_AspNetUsers_UserId",
                table: "daily_work_summary",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_daily_work_summary_AspNetUsers_UserId",
                table: "daily_work_summary");

            migrationBuilder.DropTable(
                name: "user_summary_checkpoints");

            migrationBuilder.DropIndex(
                name: "IX_daily_work_summary_UserId_SummaryDate",
                table: "daily_work_summary");

            migrationBuilder.DropColumn(
                name: "IsSystemGenerated",
                table: "DailyReport");

            migrationBuilder.DropColumn(
                name: "IsAutomatic",
                table: "daily_work_summary");

            migrationBuilder.DropColumn(
                name: "SummaryEndAt",
                table: "daily_work_summary");

            migrationBuilder.DropColumn(
                name: "SummaryStartAt",
                table: "daily_work_summary");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "daily_work_summary");
        }
    }
}

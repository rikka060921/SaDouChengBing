using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkReportProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ContainsMemberDailyData",
                table: "DailyReport",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "GeneratedContent",
                table: "DailyReport",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastHumanEditedAt",
                table: "DailyReport",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PeriodEndDate",
                table: "DailyReport",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PeriodStartDate",
                table: "DailyReport",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceSnapshotJson",
                table: "DailyReport",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "daily_report_supplements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    ReportDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "varchar(12000)", maxLength: 12000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_report_supplements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_daily_report_supplements_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_daily_report_supplements_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_daily_report_supplements_ProjectId",
                table: "daily_report_supplements",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_daily_report_supplements_UserId_ProjectId_ReportDate_Revision",
                table: "daily_report_supplements",
                columns: new[] { "UserId", "ProjectId", "ReportDate", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "daily_report_supplements");

            migrationBuilder.DropColumn(
                name: "ContainsMemberDailyData",
                table: "DailyReport");

            migrationBuilder.DropColumn(
                name: "GeneratedContent",
                table: "DailyReport");

            migrationBuilder.DropColumn(
                name: "LastHumanEditedAt",
                table: "DailyReport");

            migrationBuilder.DropColumn(
                name: "PeriodEndDate",
                table: "DailyReport");

            migrationBuilder.DropColumn(
                name: "PeriodStartDate",
                table: "DailyReport");

            migrationBuilder.DropColumn(
                name: "SourceSnapshotJson",
                table: "DailyReport");
        }
    }
}

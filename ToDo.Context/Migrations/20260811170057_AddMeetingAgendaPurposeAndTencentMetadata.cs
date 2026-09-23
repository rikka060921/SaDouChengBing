using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingAgendaPurposeAndTencentMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiDecisionsJson",
                table: "MeetingMinutes",
                type: "text",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "AiMeetingPurpose",
                table: "MeetingMinutes",
                type: "text",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "CleanContent",
                table: "MeetingMinutes",
                type: "longtext",
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ManualInputContent",
                table: "MeetingMinutes",
                type: "longtext",
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "RawTranscriptJson",
                table: "MeetingMinutes",
                type: "longtext",
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<sbyte>(
                name: "SourceType",
                table: "MeetingMinutes",
                type: "tinyint",
                nullable: false,
                defaultValue: (sbyte)0);

            migrationBuilder.AddColumn<string>(
                name: "TencentRecordFileIds",
                table: "MeetingMinutes",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "IsConfirmed",
                table: "MeetingActionItems",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MeetingAgendaId",
                table: "MeetingActionItems",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MeetingAgendas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SourceType = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsDeleted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsLocked = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    LockedByMeetingId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastModifiedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingAgendas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeetingAgendas_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingActionItems_MeetingAgendaId",
                table: "MeetingActionItems",
                column: "MeetingAgendaId");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingAgendas_ProjectId_IsDeleted_SortOrder",
                table: "MeetingAgendas",
                columns: new[] { "ProjectId", "IsDeleted", "SortOrder" });

            migrationBuilder.AddForeignKey(
                name: "FK_MeetingActionItems_MeetingAgendas_MeetingAgendaId",
                table: "MeetingActionItems",
                column: "MeetingAgendaId",
                principalTable: "MeetingAgendas",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MeetingActionItems_MeetingAgendas_MeetingAgendaId",
                table: "MeetingActionItems");

            migrationBuilder.DropTable(
                name: "MeetingAgendas");

            migrationBuilder.DropIndex(
                name: "IX_MeetingActionItems_MeetingAgendaId",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "AiDecisionsJson",
                table: "MeetingMinutes");

            migrationBuilder.DropColumn(
                name: "AiMeetingPurpose",
                table: "MeetingMinutes");

            migrationBuilder.DropColumn(
                name: "CleanContent",
                table: "MeetingMinutes");

            migrationBuilder.DropColumn(
                name: "ManualInputContent",
                table: "MeetingMinutes");

            migrationBuilder.DropColumn(
                name: "RawTranscriptJson",
                table: "MeetingMinutes");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "MeetingMinutes");

            migrationBuilder.DropColumn(
                name: "TencentRecordFileIds",
                table: "MeetingMinutes");

            migrationBuilder.DropColumn(
                name: "IsConfirmed",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "MeetingAgendaId",
                table: "MeetingActionItems");
        }
    }
}

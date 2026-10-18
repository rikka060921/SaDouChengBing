using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingPrepDraftDocumentFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Content",
                table: "MeetingPrepDrafts",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "MeetingPrepDrafts",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "MeetingTime",
                table: "MeetingPrepDrafts",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Content",
                table: "MeetingPrepDrafts");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "MeetingPrepDrafts");

            migrationBuilder.DropColumn(
                name: "MeetingTime",
                table: "MeetingPrepDrafts");
        }
    }
}

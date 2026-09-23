using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class Add_MeetingActionItem_TaskChange_Fields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActionType",
                table: "MeetingActionItems",
                type: "varchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "AfterAssigneeId",
                table: "MeetingActionItems",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AfterDeadline",
                table: "MeetingActionItems",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AfterPriority",
                table: "MeetingActionItems",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "AfterStatus",
                table: "MeetingActionItems",
                type: "varchar(30)",
                maxLength: 30,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "BeforeAssigneeId",
                table: "MeetingActionItems",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BeforeDeadline",
                table: "MeetingActionItems",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BeforePriority",
                table: "MeetingActionItems",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "BeforeStatus",
                table: "MeetingActionItems",
                type: "varchar(30)",
                maxLength: 30,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ChangeDescription",
                table: "MeetingActionItems",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActionType",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "AfterAssigneeId",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "AfterDeadline",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "AfterPriority",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "AfterStatus",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "BeforeAssigneeId",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "BeforeDeadline",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "BeforePriority",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "BeforeStatus",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "ChangeDescription",
                table: "MeetingActionItems");
        }
    }
}

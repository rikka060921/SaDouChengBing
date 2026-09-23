using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingActionSupervision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EscalationLevel",
                table: "MeetingActionItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastReminderAt",
                table: "MeetingActionItems",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSupervisedAt",
                table: "MeetingActionItems",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReminderCount",
                table: "MeetingActionItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SupervisionMessage",
                table: "MeetingActionItems",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "SupervisionStatus",
                table: "MeetingActionItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "meeting_action_supervision_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ActionItemId = table.Column<int>(type: "int", nullable: false),
                    MeetingMinutesId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    TaskId = table.Column<int>(type: "int", nullable: true),
                    EventType = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    EscalationLevel = table.Column<int>(type: "int", nullable: false),
                    EventKey = table.Column<string>(type: "varchar(160)", maxLength: 160, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RecipientIdsJson = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Message = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meeting_action_supervision_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_meeting_action_supervision_events_MeetingActionItems_ActionI~",
                        column: x => x.ActionItemId,
                        principalTable: "MeetingActionItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_meeting_action_supervision_events_ActionItemId_CreatedAt",
                table: "meeting_action_supervision_events",
                columns: new[] { "ActionItemId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_meeting_action_supervision_events_EventKey",
                table: "meeting_action_supervision_events",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_meeting_action_supervision_events_ProjectId_CreatedAt",
                table: "meeting_action_supervision_events",
                columns: new[] { "ProjectId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meeting_action_supervision_events");

            migrationBuilder.DropColumn(
                name: "EscalationLevel",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "LastReminderAt",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "LastSupervisedAt",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "ReminderCount",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "SupervisionMessage",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "SupervisionStatus",
                table: "MeetingActionItems");
        }
    }
}

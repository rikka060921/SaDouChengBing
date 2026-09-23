using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingMultiProjectSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProjectId",
                table: "MeetingActionItems",
                type: "int",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ProjectId",
                table: "meeting_action_supervision_events",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.CreateTable(
                name: "MeetingMinutesProjects",
                columns: table => new
                {
                    MeetingMinutesId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    IsPrimary = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingMinutesProjects", x => new { x.MeetingMinutesId, x.ProjectId });
                    table.ForeignKey(
                        name: "FK_MeetingMinutesProjects_MeetingMinutes_MeetingMinutesId",
                        column: x => x.MeetingMinutesId,
                        principalTable: "MeetingMinutes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MeetingMinutesProjects_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingActionItems_ProjectId",
                table: "MeetingActionItems",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingMinutesProjects_ProjectId",
                table: "MeetingMinutesProjects",
                column: "ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_MeetingActionItems_Project_ProjectId",
                table: "MeetingActionItems",
                column: "ProjectId",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MeetingActionItems_Project_ProjectId",
                table: "MeetingActionItems");

            migrationBuilder.DropTable(
                name: "MeetingMinutesProjects");

            migrationBuilder.DropIndex(
                name: "IX_MeetingActionItems_ProjectId",
                table: "MeetingActionItems");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "MeetingActionItems");

            migrationBuilder.AlterColumn<int>(
                name: "ProjectId",
                table: "meeting_action_supervision_events",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}

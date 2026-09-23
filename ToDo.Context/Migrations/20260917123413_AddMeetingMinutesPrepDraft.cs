using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingMinutesPrepDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PrepDraftId",
                table: "MeetingMinutes",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeetingMinutes_PrepDraftId",
                table: "MeetingMinutes",
                column: "PrepDraftId");

            migrationBuilder.AddForeignKey(
                name: "FK_MeetingMinutes_MeetingPrepDrafts_PrepDraftId",
                table: "MeetingMinutes",
                column: "PrepDraftId",
                principalTable: "MeetingPrepDrafts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MeetingMinutes_MeetingPrepDrafts_PrepDraftId",
                table: "MeetingMinutes");

            migrationBuilder.DropIndex(
                name: "IX_MeetingMinutes_PrepDraftId",
                table: "MeetingMinutes");

            migrationBuilder.DropColumn(
                name: "PrepDraftId",
                table: "MeetingMinutes");
        }
    }
}

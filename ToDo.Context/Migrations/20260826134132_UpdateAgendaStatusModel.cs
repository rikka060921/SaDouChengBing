using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class UpdateAgendaStatusModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LockedByMeetingId",
                table: "MeetingAgendas",
                newName: "ArchivedByMeetingId");

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "MeetingAgendas",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "MeetingAgendas",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Preserve the legacy lock state before removing IsLocked. Locked agendas
            // represented historical meeting records and must become archived rather
            // than silently returning to the active agenda pool.
            migrationBuilder.Sql(
                """
                UPDATE `MeetingAgendas`
                SET `Status` = 1,
                    `ArchivedAt` = COALESCE(`LastModifiedAt`, `CreatedAt`)
                WHERE `IsLocked` = 1;
                """);

            // Backfill the new many-to-many relation while the legacy meeting id is
            // still available. INSERT IGNORE also makes this safe for partially
            // migrated databases that already contain some relations.
            migrationBuilder.Sql(
                """
                INSERT IGNORE INTO `MeetingAgendaRelations`
                    (`MeetingMinutesId`, `MeetingAgendaId`, `CreatedAt`)
                SELECT `ArchivedByMeetingId`, `Id`, COALESCE(`LastModifiedAt`, `CreatedAt`)
                FROM `MeetingAgendas`
                WHERE `IsLocked` = 1
                  AND `ArchivedByMeetingId` IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "IsLocked",
                table: "MeetingAgendas");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ArchivedByMeetingId",
                table: "MeetingAgendas",
                newName: "LockedByMeetingId");

            migrationBuilder.AddColumn<bool>(
                name: "IsLocked",
                table: "MeetingAgendas",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                """
                UPDATE `MeetingAgendas`
                SET `IsLocked` = 1
                WHERE `Status` = 1;
                """);

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "MeetingAgendas");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "MeetingAgendas");
        }
    }
}

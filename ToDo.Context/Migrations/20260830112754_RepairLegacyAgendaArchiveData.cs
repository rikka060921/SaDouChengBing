using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class RepairLegacyAgendaArchiveData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Repair databases that already applied UpdateAgendaStatusModel before
            // its data-preserving backfill was added. ArchivedByMeetingId is the
            // remaining durable signal that the legacy agenda was locked by a meeting.
            migrationBuilder.Sql(
                """
                UPDATE `MeetingAgendas`
                SET `Status` = 1,
                    `ArchivedAt` = COALESCE(`ArchivedAt`, `LastModifiedAt`, `CreatedAt`)
                WHERE `ArchivedByMeetingId` IS NOT NULL;
                """);

            migrationBuilder.Sql(
                """
                INSERT IGNORE INTO `MeetingAgendaRelations`
                    (`MeetingMinutesId`, `MeetingAgendaId`, `CreatedAt`)
                SELECT `ArchivedByMeetingId`, `Id`, COALESCE(`ArchivedAt`, `LastModifiedAt`, `CreatedAt`)
                FROM `MeetingAgendas`
                WHERE `ArchivedByMeetingId` IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally irreversible: reverting repaired archive data would
            // recreate the data-loss condition this migration corrects.
        }
    }
}

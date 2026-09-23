using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingAgendaRelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MeetingAgendaRelations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    MeetingMinutesId = table.Column<int>(type: "int", nullable: false),
                    MeetingAgendaId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingAgendaRelations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeetingAgendaRelations_MeetingAgendas_MeetingAgendaId",
                        column: x => x.MeetingAgendaId,
                        principalTable: "MeetingAgendas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MeetingAgendaRelations_MeetingMinutes_MeetingMinutesId",
                        column: x => x.MeetingMinutesId,
                        principalTable: "MeetingMinutes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingAgendaRelations_MeetingAgendaId",
                table: "MeetingAgendaRelations",
                column: "MeetingAgendaId");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingAgendaRelations_MeetingMinutesId_MeetingAgendaId",
                table: "MeetingAgendaRelations",
                columns: new[] { "MeetingMinutesId", "MeetingAgendaId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MeetingAgendaRelations");
        }
    }
}

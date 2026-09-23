using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <summary>
    /// Compatibility marker for a migration that was accidentally generated as a
    /// second full baseline after the application already had a migration history.
    /// Keeping the migration id preserves upgrade ordering without recreating every
    /// existing table on deployed databases.
    /// </summary>
    public partial class InitDB : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}

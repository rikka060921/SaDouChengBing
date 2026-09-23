using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class RepairMeetingAndDocumentSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Correct databases created by the original category migration, whose
            // string annotation did not produce AUTO_INCREMENT.
            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "document_categories",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int")
                .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn);

            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "document_categories",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // These fields were present in the EF snapshot and entity but were
            // accidentally omitted from every previous Up migration.
            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "MeetingActionItems",
                type: "varchar(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "MeetingActionItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "MeetingActionItems",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Medium");

            migrationBuilder.AddColumn<string>(
                name: "GroupName",
                table: "MeetingActionItems",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaskStatus",
                table: "MeetingActionItems",
                type: "varchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "NotStarted");

            migrationBuilder.Sql(
                "UPDATE `MeetingActionItems` SET `Title` = LEFT(`Content`, 255) WHERE `Title` = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "IsDefault", table: "document_categories");
            migrationBuilder.DropColumn(name: "Title", table: "MeetingActionItems");
            migrationBuilder.DropColumn(name: "Description", table: "MeetingActionItems");
            migrationBuilder.DropColumn(name: "Priority", table: "MeetingActionItems");
            migrationBuilder.DropColumn(name: "GroupName", table: "MeetingActionItems");
            migrationBuilder.DropColumn(name: "TaskStatus", table: "MeetingActionItems");
        }
    }
}

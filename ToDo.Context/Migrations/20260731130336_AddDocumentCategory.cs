using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IsDefault is intentionally added by the follow-up repair migration.
            // This keeps fresh databases and interrupted deployments on one path.
            migrationBuilder.CreateTable(
                name: "document_categories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValue: DateTime.UtcNow)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_categories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_document_categories_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_categories_ProjectId_Name",
                table: "document_categories",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.AddColumn<int?>(
                name: "CategoryId",
                table: "project_documents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CategoryName",
                table: "project_documents",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int?>(
                name: "CategoryId",
                table: "agent_document_permissions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int?>(
                name: "CategoryId",
                table: "agent_document_access_logs",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_document_permissions_CategoryId",
                table: "agent_document_permissions",
                column: "CategoryId");

            // Create the replacement index first. Dropping the old index first
            // breaks the existing ProjectId foreign key on MySQL.
            migrationBuilder.CreateIndex(
                name: "IX_agent_doc_perm_proj_agent_cat_catid",
                table: "agent_document_permissions",
                columns: new[] { "ProjectId", "AgentKey", "Category", "CategoryId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_agent_document_permissions_document_categories_CategoryId",
                table: "agent_document_permissions",
                column: "CategoryId",
                principalTable: "document_categories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropIndex(
                name: "IX_agent_document_permissions_ProjectId_AgentKey_Category",
                table: "agent_document_permissions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_agent_document_permissions_document_categories_CategoryId",
                table: "agent_document_permissions");

            migrationBuilder.DropIndex(
                name: "IX_agent_document_permissions_CategoryId",
                table: "agent_document_permissions");

            migrationBuilder.DropIndex(
                name: "IX_agent_doc_perm_proj_agent_cat_catid",
                table: "agent_document_permissions");

            migrationBuilder.CreateIndex(
                name: "IX_agent_document_permissions_ProjectId_AgentKey_Category",
                table: "agent_document_permissions",
                columns: new[] { "ProjectId", "AgentKey", "Category" },
                unique: true);

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "agent_document_access_logs");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "agent_document_permissions");

            migrationBuilder.DropColumn(
                name: "CategoryName",
                table: "project_documents");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "project_documents");

            migrationBuilder.DropTable(name: "document_categories");
        }
    }
}

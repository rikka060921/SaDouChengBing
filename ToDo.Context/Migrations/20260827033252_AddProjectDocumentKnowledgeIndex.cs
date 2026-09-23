using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectDocumentKnowledgeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExtractedCharacterCount",
                table: "project_documents",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IndexAttemptCount",
                table: "project_documents",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "IndexError",
                table: "project_documents",
                type: "varchar(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "IndexLockedAt",
                table: "project_documents",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IndexNextRetryAt",
                table: "project_documents",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IndexStatus",
                table: "project_documents",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "IndexedAt",
                table: "project_documents",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_document_chunks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ProjectDocumentId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    ChunkIndex = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Heading = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CharacterCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_document_chunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_project_document_chunks_project_documents_ProjectDocumentId",
                        column: x => x.ProjectDocumentId,
                        principalTable: "project_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_project_document_chunks_ProjectDocumentId_ChunkIndex",
                table: "project_document_chunks",
                columns: new[] { "ProjectDocumentId", "ChunkIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_document_chunks_ProjectId_ProjectDocumentId",
                table: "project_document_chunks",
                columns: new[] { "ProjectId", "ProjectDocumentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_document_chunks");

            migrationBuilder.DropColumn(
                name: "ExtractedCharacterCount",
                table: "project_documents");

            migrationBuilder.DropColumn(
                name: "IndexAttemptCount",
                table: "project_documents");

            migrationBuilder.DropColumn(
                name: "IndexError",
                table: "project_documents");

            migrationBuilder.DropColumn(
                name: "IndexLockedAt",
                table: "project_documents");

            migrationBuilder.DropColumn(
                name: "IndexNextRetryAt",
                table: "project_documents");

            migrationBuilder.DropColumn(
                name: "IndexStatus",
                table: "project_documents");

            migrationBuilder.DropColumn(
                name: "IndexedAt",
                table: "project_documents");
        }
    }
}

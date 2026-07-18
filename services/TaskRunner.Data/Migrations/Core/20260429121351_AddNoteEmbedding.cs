using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskRunner.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteEmbedding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NoteEmbeddings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VaultId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    NotePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    VectorJson = table.Column<string>(type: "TEXT", nullable: false),
                    Dimensions = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NoteEmbeddings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NoteEmbeddings_VaultId_NotePath",
                table: "NoteEmbeddings",
                columns: new[] { "VaultId", "NotePath" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NoteEmbeddings");
        }
    }
}

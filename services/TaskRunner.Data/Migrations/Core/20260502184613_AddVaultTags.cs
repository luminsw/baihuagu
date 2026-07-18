using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskRunner.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVaultTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "Vaults",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ChatMemoryEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    UserSummary = table.Column<string>(type: "TEXT", nullable: false),
                    AssistantSummary = table.Column<string>(type: "TEXT", nullable: false),
                    UserContent = table.Column<string>(type: "TEXT", nullable: false),
                    AssistantContent = table.Column<string>(type: "TEXT", nullable: false),
                    VectorJson = table.Column<string>(type: "TEXT", nullable: true),
                    Dimensions = table.Column<int>(type: "INTEGER", nullable: false),
                    EstimatedTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMemoryEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMemoryEntries_SessionId_Round",
                table: "ChatMemoryEntries",
                columns: new[] { "SessionId", "Round" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatMemoryEntries");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "Vaults");
        }
    }
}

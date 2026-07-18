using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskRunner.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCardReviewState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CardReviewStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LearnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    VaultId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CardId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IntervalDays = table.Column<int>(type: "INTEGER", nullable: false),
                    NextReviewDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConsecutiveRemember = table.Column<int>(type: "INTEGER", nullable: false),
                    LastResult = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    TotalReviews = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardReviewStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CardReviewStates_LearnerId_VaultId_CardId",
                table: "CardReviewStates",
                columns: new[] { "LearnerId", "VaultId", "CardId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CardReviewStates_LearnerId_VaultId_NextReviewDate",
                table: "CardReviewStates",
                columns: new[] { "LearnerId", "VaultId", "NextReviewDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CardReviewStates");
        }
    }
}

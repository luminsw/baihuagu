using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskRunner.Migrations
{
    /// <inheritdoc />
    public partial class AddModelTierAndEmbeddingConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Tier",
                table: "AiProviderSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "EmbeddingConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    EncryptedApiKey = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Dimensions = table.Column<int>(type: "INTEGER", nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    LastIndexBuildAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmbeddingConfigs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmbeddingConfigs_ProviderId",
                table: "EmbeddingConfigs",
                column: "ProviderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmbeddingConfigs");

            migrationBuilder.DropColumn(
                name: "Tier",
                table: "AiProviderSettings");
        }
    }
}

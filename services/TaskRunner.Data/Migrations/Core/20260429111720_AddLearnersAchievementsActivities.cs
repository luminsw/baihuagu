using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskRunner.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLearnersAchievementsActivities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Achievements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LearnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Icon = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Tier = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    UnlockedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Achievements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LearnerProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    AvatarEmoji = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LearnerProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StudyActivities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LearnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    VaultId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ActivityType = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CardId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Result = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StudyActivities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Achievements_LearnerId_Key",
                table: "Achievements",
                columns: new[] { "LearnerId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LearnerProfiles_Name",
                table: "LearnerProfiles",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_StudyActivities_LearnerId",
                table: "StudyActivities",
                column: "LearnerId");

            migrationBuilder.CreateIndex(
                name: "IX_StudyActivities_LearnerId_VaultId_CreatedAt",
                table: "StudyActivities",
                columns: new[] { "LearnerId", "VaultId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Achievements");

            migrationBuilder.DropTable(
                name: "LearnerProfiles");

            migrationBuilder.DropTable(
                name: "StudyActivities");
        }
    }
}

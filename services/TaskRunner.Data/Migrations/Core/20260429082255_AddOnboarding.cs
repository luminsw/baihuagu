using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskRunner.Data.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InitTaskProgresses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    IsCompleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    IsSkipped = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InitTaskProgresses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OnboardingStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IsCompleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardingStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InitTaskProgresses_TaskId",
                table: "InitTaskProgresses",
                column: "TaskId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InitTaskProgresses");

            migrationBuilder.DropTable(
                name: "OnboardingStates");
        }
    }
}

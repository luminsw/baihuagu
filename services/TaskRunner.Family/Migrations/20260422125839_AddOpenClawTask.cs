using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskRunner.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenClawTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ServerInstanceId",
                table: "ServerAddressSettings",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "OpenClawTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, defaultValue: "pending"),
                    ReportPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Result = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenClawTasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpenClawTasks_CreatedAt",
                table: "OpenClawTasks",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OpenClawTasks_Status",
                table: "OpenClawTasks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_OpenClawTasks_TaskId",
                table: "OpenClawTasks",
                column: "TaskId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpenClawTasks");

            migrationBuilder.DropColumn(
                name: "ServerInstanceId",
                table: "ServerAddressSettings");
        }
    }
}

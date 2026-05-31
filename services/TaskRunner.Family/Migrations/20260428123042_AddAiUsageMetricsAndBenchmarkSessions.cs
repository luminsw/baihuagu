using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskRunner.Migrations
{
    /// <inheritdoc />
    public partial class AddAiUsageMetricsAndBenchmarkSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Note: Domain and ServerInstanceId columns already exist in the database
            // from a previous unrecorded migration. Skipping to avoid duplicate column error.

            migrationBuilder.CreateTable(
                name: "AiUsageMetrics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CalledAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ProviderName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    LatencyMs = table.Column<long>(type: "INTEGER", nullable: false),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    TokensPerSecond = table.Column<double>(type: "REAL", nullable: true),
                    IsSuccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BenchmarkSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TestedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    ModelName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ResultsJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    AvgTokensPerSecond = table.Column<double>(type: "REAL", nullable: false),
                    AvgLatencyMs = table.Column<double>(type: "REAL", nullable: false),
                    AvgQualityScore = table.Column<double>(type: "REAL", nullable: false),
                    CompletionRate = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenchmarkSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_CalledAt",
                table: "AiUsageMetrics",
                column: "CalledAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_ModelId",
                table: "AiUsageMetrics",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_Operation",
                table: "AiUsageMetrics",
                column: "Operation");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageMetrics_ProviderId",
                table: "AiUsageMetrics",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_BenchmarkSessions_Category",
                table: "BenchmarkSessions",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_BenchmarkSessions_SessionId",
                table: "BenchmarkSessions",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BenchmarkSessions_TestedAt",
                table: "BenchmarkSessions",
                column: "TestedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiUsageMetrics");

            migrationBuilder.DropTable(
                name: "BenchmarkSessions");

            // Note: Skipped dropping Domain and reverting ServerInstanceId
            // as they were not added by this migration.
        }
    }
}

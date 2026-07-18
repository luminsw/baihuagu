using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskRunner.Data.Migrations.Core
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiProviderSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ProviderName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    EncryptedApiKey = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsMain = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    ModelsJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProviderSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuthorizedDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AccessToken = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, defaultValue: "Authorized"),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    LastSyncTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AuthorizedTime = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    TokenExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthorizedDevices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerAddressSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false, defaultValue: ""),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerAddressSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, defaultValue: "Pending"),
                    Input = table.Column<string>(type: "TEXT", nullable: true),
                    Output = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Progress = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    ProgressMessage = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Vaults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VaultId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vaults", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiProviderSettings_IsMain",
                table: "AiProviderSettings",
                column: "IsMain");

            migrationBuilder.CreateIndex(
                name: "IX_AiProviderSettings_ProviderId",
                table: "AiProviderSettings",
                column: "ProviderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizedDevices_AccessToken",
                table: "AuthorizedDevices",
                column: "AccessToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizedDevices_DeviceId",
                table: "AuthorizedDevices",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizedDevices_Status",
                table: "AuthorizedDevices",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_CreatedAt",
                table: "Tasks",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Status",
                table: "Tasks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_TaskId",
                table: "Tasks",
                column: "TaskId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vaults_IsActive",
                table: "Vaults",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Vaults_VaultId",
                table: "Vaults",
                column: "VaultId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiProviderSettings");

            migrationBuilder.DropTable(
                name: "AuthorizedDevices");

            migrationBuilder.DropTable(
                name: "ServerAddressSettings");

            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "Vaults");
        }
    }
}

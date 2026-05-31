using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskRunner.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVaultPaidAndDeviceQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPaid",
                table: "Vaults",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "SyncType",
                table: "DeviceSyncLogs",
                type: "TEXT",
                maxLength: 50,
                nullable: false,
                defaultValue: "manifest",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<DateTime>(
                name: "SyncTime",
                table: "DeviceSyncLogs",
                type: "TEXT",
                nullable: false,
                defaultValueSql: "datetime('now')",
                oldClrType: typeof(DateTime),
                oldType: "TEXT");

            migrationBuilder.CreateTable(
                name: "DeviceDailySyncs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    VaultId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    SyncDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SyncCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UsedPaidQuota = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceDailySyncs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceQuotas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PaidSyncQuota = table.Column<int>(type: "INTEGER", nullable: false),
                    AiBuildQuota = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalSpent = table.Column<decimal>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceQuotas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IapPurchaseRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProductId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PurchaseToken = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", nullable: true),
                    QuotaAdded = table.Column<int>(type: "INTEGER", nullable: false),
                    QuotaType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    IsVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                    VerifyResponse = table.Column<string>(type: "TEXT", nullable: true),
                    VerifyError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IapPurchaseRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSyncLogs_DeviceId",
                table: "DeviceSyncLogs",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSyncLogs_SyncTime",
                table: "DeviceSyncLogs",
                column: "SyncTime");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDailySyncs_DeviceId_VaultId_SyncDate",
                table: "DeviceDailySyncs",
                columns: new[] { "DeviceId", "VaultId", "SyncDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceQuotas_DeviceId",
                table: "DeviceQuotas",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IapPurchaseRecords_DeviceId",
                table: "IapPurchaseRecords",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_IapPurchaseRecords_PurchaseToken",
                table: "IapPurchaseRecords",
                column: "PurchaseToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceDailySyncs");

            migrationBuilder.DropTable(
                name: "DeviceQuotas");

            migrationBuilder.DropTable(
                name: "IapPurchaseRecords");

            migrationBuilder.DropIndex(
                name: "IX_DeviceSyncLogs_DeviceId",
                table: "DeviceSyncLogs");

            migrationBuilder.DropIndex(
                name: "IX_DeviceSyncLogs_SyncTime",
                table: "DeviceSyncLogs");

            migrationBuilder.DropColumn(
                name: "IsPaid",
                table: "Vaults");

            migrationBuilder.AlterColumn<string>(
                name: "SyncType",
                table: "DeviceSyncLogs",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 50,
                oldDefaultValue: "manifest");

            migrationBuilder.AlterColumn<DateTime>(
                name: "SyncTime",
                table: "DeviceSyncLogs",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "TEXT",
                oldDefaultValueSql: "datetime('now')");
        }
    }
}

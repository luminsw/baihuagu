using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskRunner.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveQuotaAndIapTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceQuotas");

            migrationBuilder.DropTable(
                name: "DeviceDailySyncs");

            migrationBuilder.DropTable(
                name: "IapPurchaseRecords");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
                    TotalSpent = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceQuotas", x => x.Id);
                });

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
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceDailySyncs", x => x.Id);
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
                    QuotaType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    QuotaAmount = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IsVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IapPurchaseRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceQuotas_DeviceId",
                table: "DeviceQuotas",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceDailySyncs_DeviceId_VaultId_SyncDate",
                table: "DeviceDailySyncs",
                columns: new[] { "DeviceId", "VaultId", "SyncDate" },
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
    }
}

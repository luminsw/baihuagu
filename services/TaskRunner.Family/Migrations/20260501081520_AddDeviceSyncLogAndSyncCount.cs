using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskRunner.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceSyncLogAndSyncCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSyncTime",
                table: "AuthorizedDevices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SyncCount",
                table: "AuthorizedDevices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DeviceSyncLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", nullable: true),
                    VaultId = table.Column<string>(type: "TEXT", nullable: true),
                    FileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SyncType = table.Column<string>(type: "TEXT", nullable: false),
                    SyncTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceSyncLogs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceSyncLogs");

            migrationBuilder.DropColumn(
                name: "FirstSyncTime",
                table: "AuthorizedDevices");

            migrationBuilder.DropColumn(
                name: "SyncCount",
                table: "AuthorizedDevices");
        }
    }
}

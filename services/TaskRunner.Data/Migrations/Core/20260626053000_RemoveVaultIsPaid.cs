using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskRunner.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveVaultIsPaid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite 不支持直�?DROP COLUMN，EF Core 会通过重建表来实现
            migrationBuilder.DropColumn(
                name: "IsPaid",
                table: "Vaults");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPaid",
                table: "Vaults",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
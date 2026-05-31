using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskRunner.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnthropicAndVaultSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Vaults",
                type: "TEXT",
                nullable: false,
                defaultValue: "local");

            migrationBuilder.AddColumn<string>(
                name: "AnthropicBaseUrl",
                table: "AiProviderSettings",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Source",
                table: "Vaults");

            migrationBuilder.DropColumn(
                name: "AnthropicBaseUrl",
                table: "AiProviderSettings");
        }
    }
}

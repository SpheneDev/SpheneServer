using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddCommunityDestination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "destination_json",
                table: "syncshell_welcome_pages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "destination_published",
                table: "syncshell_welcome_pages",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "destination_json",
                table: "syncshell_welcome_pages");

            migrationBuilder.DropColumn(
                name: "destination_published",
                table: "syncshell_welcome_pages");
        }
    }
}

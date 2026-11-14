using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class SyncshellProfileFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "accent_color",
                table: "syncshell_welcome_pages",
                type: "character varying(9)",
                maxLength: 9,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "avatar_content_type",
                table: "syncshell_welcome_pages",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "avatar_file_name",
                table: "syncshell_welcome_pages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "avatar_image_base64",
                table: "syncshell_welcome_pages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "avatar_size",
                table: "syncshell_welcome_pages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sections_json",
                table: "syncshell_welcome_pages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tagline",
                table: "syncshell_welcome_pages",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "accent_color",
                table: "syncshell_welcome_pages");

            migrationBuilder.DropColumn(
                name: "avatar_content_type",
                table: "syncshell_welcome_pages");

            migrationBuilder.DropColumn(
                name: "avatar_file_name",
                table: "syncshell_welcome_pages");

            migrationBuilder.DropColumn(
                name: "avatar_image_base64",
                table: "syncshell_welcome_pages");

            migrationBuilder.DropColumn(
                name: "avatar_size",
                table: "syncshell_welcome_pages");

            migrationBuilder.DropColumn(
                name: "sections_json",
                table: "syncshell_welcome_pages");

            migrationBuilder.DropColumn(
                name: "tagline",
                table: "syncshell_welcome_pages");
        }
    }
}

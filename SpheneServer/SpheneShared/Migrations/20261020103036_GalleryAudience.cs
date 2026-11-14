using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class GalleryAudience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "allowed_uids_json",
                table: "syncshell_community_gallery_albums",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "audience",
                table: "syncshell_community_gallery_albums",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "allowed_uids_json",
                table: "syncshell_community_gallery",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "audience",
                table: "syncshell_community_gallery",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allowed_uids_json",
                table: "syncshell_community_gallery_albums");

            migrationBuilder.DropColumn(
                name: "audience",
                table: "syncshell_community_gallery_albums");

            migrationBuilder.DropColumn(
                name: "allowed_uids_json",
                table: "syncshell_community_gallery");

            migrationBuilder.DropColumn(
                name: "audience",
                table: "syncshell_community_gallery");
        }
    }
}

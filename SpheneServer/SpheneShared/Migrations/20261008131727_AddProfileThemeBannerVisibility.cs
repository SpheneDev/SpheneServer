using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileThemeBannerVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "base64banner_image",
                table: "user_profile_data",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "profile_theme",
                table: "user_profile_data",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "profile_visibility",
                table: "user_profile_data",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "base64banner_image",
                table: "user_profile_data");

            migrationBuilder.DropColumn(
                name: "profile_theme",
                table: "user_profile_data");

            migrationBuilder.DropColumn(
                name: "profile_visibility",
                table: "user_profile_data");
        }
    }
}

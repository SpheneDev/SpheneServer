using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileStatusAndTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "profile_tags",
                table: "user_profile_data",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status_message",
                table: "user_profile_data",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "profile_tags",
                table: "user_profile_data");

            migrationBuilder.DropColumn(
                name: "status_message",
                table: "user_profile_data");
        }
    }
}

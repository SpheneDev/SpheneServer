using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class GifBetaEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "gif_beta_enabled",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "gif_beta_enabled",
                table: "users");
        }
    }
}

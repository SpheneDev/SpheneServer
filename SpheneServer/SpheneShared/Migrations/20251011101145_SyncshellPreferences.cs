using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class SyncshellPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "prefer_indoor_syncshells",
                table: "user_housing_properties",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "prefer_outdoor_syncshells",
                table: "user_housing_properties",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "prefer_indoor_syncshells",
                table: "user_housing_properties");

            migrationBuilder.DropColumn(
                name: "prefer_outdoor_syncshells",
                table: "user_housing_properties");
        }
    }
}

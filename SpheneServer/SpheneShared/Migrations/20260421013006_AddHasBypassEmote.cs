using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddHasBypassEmote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bypass_emote_data",
                table: "chara_data");

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "chara_data_files",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "chara_data_file_swaps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "has_bypass_emote",
                table: "chara_data",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_active",
                table: "chara_data_files");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "chara_data_file_swaps");

            migrationBuilder.DropColumn(
                name: "has_bypass_emote",
                table: "chara_data");

            migrationBuilder.AddColumn<string>(
                name: "bypass_emote_data",
                table: "chara_data",
                type: "text",
                nullable: true);
        }
    }
}

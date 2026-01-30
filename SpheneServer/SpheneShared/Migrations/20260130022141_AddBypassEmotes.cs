using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddBypassEmotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bypass_emote_data",
                table: "chara_data",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bypass_emote_data",
                table: "chara_data");
        }
    }
}

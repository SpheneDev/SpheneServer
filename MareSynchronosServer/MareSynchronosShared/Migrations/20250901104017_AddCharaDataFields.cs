using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class AddCharaDataFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "heels_data",
                table: "chara_data",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "honorific_data",
                table: "chara_data",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "moodles_data",
                table: "chara_data",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pet_names_data",
                table: "chara_data",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "heels_data",
                table: "chara_data");

            migrationBuilder.DropColumn(
                name: "honorific_data",
                table: "chara_data");

            migrationBuilder.DropColumn(
                name: "moodles_data",
                table: "chara_data");

            migrationBuilder.DropColumn(
                name: "pet_names_data",
                table: "chara_data");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddEventDestination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "destination_json",
                table: "syncshell_community_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "destination_json",
                table: "community_event_exceptions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "destination_json",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "destination_json",
                table: "community_event_exceptions");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class DisableVFXInDuty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "disable_vfx_in_duty",
                table: "user_permission_sets",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "disable_vfx_in_duty",
                table: "user_permission_sets");
        }
    }
}

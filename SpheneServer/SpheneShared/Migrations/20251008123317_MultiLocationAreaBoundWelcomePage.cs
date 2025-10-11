using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class MultiLocationAreaBoundWelcomePage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "syncshell_welcome_pages",
                columns: table => new
                {
                    group_gid = table.Column<string>(type: "character varying(20)", nullable: false),
                    welcome_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    welcome_image_base64 = table.Column<string>(type: "text", nullable: true),
                    image_file_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    image_content_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    image_size = table.Column<long>(type: "bigint", nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    show_on_join = table.Column<bool>(type: "boolean", nullable: false),
                    show_on_area_bound_join = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_syncshell_welcome_pages", x => x.group_gid);
                    table.ForeignKey(
                        name: "fk_syncshell_welcome_pages_groups_group_gid",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_welcome_pages_is_enabled",
                table: "syncshell_welcome_pages",
                column: "is_enabled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "syncshell_welcome_pages");
        }
    }
}

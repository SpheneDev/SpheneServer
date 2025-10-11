using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddAreaBoundSyncshell : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "area_bound_syncshells",
                columns: table => new
                {
                    group_gid = table.Column<string>(type: "character varying(20)", nullable: false),
                    server_id = table.Column<int>(type: "integer", nullable: false),
                    map_id = table.Column<long>(type: "bigint", nullable: false),
                    territory_id = table.Column<long>(type: "bigint", nullable: false),
                    division_id = table.Column<long>(type: "bigint", nullable: false),
                    ward_id = table.Column<int>(type: "integer", nullable: false),
                    house_id = table.Column<int>(type: "integer", nullable: false),
                    room_id = table.Column<byte>(type: "smallint", nullable: false),
                    auto_broadcast_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    max_auto_join_users = table.Column<int>(type: "integer", nullable: false),
                    area_matching_mode = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_area_bound_syncshells", x => x.group_gid);
                    table.ForeignKey(
                        name: "fk_area_bound_syncshells_groups_group_gid",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_area_bound_syncshells_auto_broadcast_enabled",
                table: "area_bound_syncshells",
                column: "auto_broadcast_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_area_bound_syncshells_server_id_map_id",
                table: "area_bound_syncshells",
                columns: new[] { "server_id", "map_id" });

            migrationBuilder.CreateIndex(
                name: "ix_area_bound_syncshells_server_id_territory_id",
                table: "area_bound_syncshells",
                columns: new[] { "server_id", "territory_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "area_bound_syncshells");
        }
    }
}

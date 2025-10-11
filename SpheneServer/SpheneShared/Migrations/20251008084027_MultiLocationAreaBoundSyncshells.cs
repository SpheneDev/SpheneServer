using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class MultiLocationAreaBoundSyncshells : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_area_bound_syncshells_server_id_map_id",
                table: "area_bound_syncshells");

            migrationBuilder.DropIndex(
                name: "ix_area_bound_syncshells_server_id_territory_id",
                table: "area_bound_syncshells");

            migrationBuilder.DropColumn(
                name: "area_matching_mode",
                table: "area_bound_syncshells");

            migrationBuilder.DropColumn(
                name: "division_id",
                table: "area_bound_syncshells");

            migrationBuilder.DropColumn(
                name: "house_id",
                table: "area_bound_syncshells");

            migrationBuilder.DropColumn(
                name: "map_id",
                table: "area_bound_syncshells");

            migrationBuilder.DropColumn(
                name: "room_id",
                table: "area_bound_syncshells");

            migrationBuilder.DropColumn(
                name: "server_id",
                table: "area_bound_syncshells");

            migrationBuilder.DropColumn(
                name: "territory_id",
                table: "area_bound_syncshells");

            migrationBuilder.DropColumn(
                name: "ward_id",
                table: "area_bound_syncshells");

            migrationBuilder.AddColumn<string>(
                name: "custom_join_message",
                table: "area_bound_syncshells",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "notify_on_user_enter",
                table: "area_bound_syncshells",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "notify_on_user_leave",
                table: "area_bound_syncshells",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "require_owner_presence",
                table: "area_bound_syncshells",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "area_bound_locations",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    group_gid = table.Column<string>(type: "character varying(20)", nullable: true),
                    server_id = table.Column<int>(type: "integer", nullable: false),
                    map_id = table.Column<long>(type: "bigint", nullable: false),
                    territory_id = table.Column<long>(type: "bigint", nullable: false),
                    division_id = table.Column<long>(type: "bigint", nullable: false),
                    ward_id = table.Column<int>(type: "integer", nullable: false),
                    house_id = table.Column<int>(type: "integer", nullable: false),
                    room_id = table.Column<byte>(type: "smallint", nullable: false),
                    matching_mode = table.Column<int>(type: "integer", nullable: false),
                    location_name = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_area_bound_locations", x => x.id);
                    table.ForeignKey(
                        name: "fk_area_bound_locations_area_bound_syncshells_group_gid",
                        column: x => x.group_gid,
                        principalTable: "area_bound_syncshells",
                        principalColumn: "group_gid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_area_bound_locations_group_gid",
                table: "area_bound_locations",
                column: "group_gid");

            migrationBuilder.CreateIndex(
                name: "ix_area_bound_locations_server_id_map_id",
                table: "area_bound_locations",
                columns: new[] { "server_id", "map_id" });

            migrationBuilder.CreateIndex(
                name: "ix_area_bound_locations_server_id_territory_id",
                table: "area_bound_locations",
                columns: new[] { "server_id", "territory_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "area_bound_locations");

            migrationBuilder.DropColumn(
                name: "custom_join_message",
                table: "area_bound_syncshells");

            migrationBuilder.DropColumn(
                name: "notify_on_user_enter",
                table: "area_bound_syncshells");

            migrationBuilder.DropColumn(
                name: "notify_on_user_leave",
                table: "area_bound_syncshells");

            migrationBuilder.DropColumn(
                name: "require_owner_presence",
                table: "area_bound_syncshells");

            migrationBuilder.AddColumn<int>(
                name: "area_matching_mode",
                table: "area_bound_syncshells",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "division_id",
                table: "area_bound_syncshells",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "house_id",
                table: "area_bound_syncshells",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "map_id",
                table: "area_bound_syncshells",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<byte>(
                name: "room_id",
                table: "area_bound_syncshells",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<int>(
                name: "server_id",
                table: "area_bound_syncshells",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "territory_id",
                table: "area_bound_syncshells",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "ward_id",
                table: "area_bound_syncshells",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_area_bound_syncshells_server_id_map_id",
                table: "area_bound_syncshells",
                columns: new[] { "server_id", "map_id" });

            migrationBuilder.CreateIndex(
                name: "ix_area_bound_syncshells_server_id_territory_id",
                table: "area_bound_syncshells",
                columns: new[] { "server_id", "territory_id" });
        }
    }
}

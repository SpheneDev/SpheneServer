using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class SyncshellGalleryAlbums : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "album_id",
                table: "syncshell_community_gallery",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "status",
                table: "syncshell_community_gallery",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "uploaded_by_uid",
                table: "syncshell_community_gallery",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "syncshell_community_gallery_albums",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    group_gid = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    linked_event_id = table.Column<long>(type: "bigint", nullable: true),
                    cover_image_id = table.Column<long>(type: "bigint", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_syncshell_community_gallery_albums", x => x.id);
                    table.ForeignKey(
                        name: "fk_syncshell_community_gallery_albums_groups_group_gid",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_community_gallery_album_id",
                table: "syncshell_community_gallery",
                column: "album_id");

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_community_gallery_albums_group_gid",
                table: "syncshell_community_gallery_albums",
                column: "group_gid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "syncshell_community_gallery_albums");

            migrationBuilder.DropIndex(
                name: "ix_syncshell_community_gallery_album_id",
                table: "syncshell_community_gallery");

            migrationBuilder.DropColumn(
                name: "album_id",
                table: "syncshell_community_gallery");

            migrationBuilder.DropColumn(
                name: "status",
                table: "syncshell_community_gallery");

            migrationBuilder.DropColumn(
                name: "uploaded_by_uid",
                table: "syncshell_community_gallery");
        }
    }
}

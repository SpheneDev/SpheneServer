using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class SyncshellGalleryImageLikes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "syncshell_community_gallery_image_likes",
                columns: table => new
                {
                    image_id = table.Column<long>(type: "bigint", nullable: false),
                    user_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    group_gid = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_syncshell_community_gallery_image_likes", x => new { x.image_id, x.user_uid });
                    table.ForeignKey(
                        name: "fk_syncshell_community_gallery_image_likes_groups_group_gid",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid");
                    table.ForeignKey(
                        name: "fk_syncshell_community_gallery_image_likes_syncshell_community",
                        column: x => x.image_id,
                        principalTable: "syncshell_community_gallery",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_community_gallery_image_likes_group_gid",
                table: "syncshell_community_gallery_image_likes",
                column: "group_gid");

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_community_gallery_image_likes_user_uid",
                table: "syncshell_community_gallery_image_likes",
                column: "user_uid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "syncshell_community_gallery_image_likes");
        }
    }
}

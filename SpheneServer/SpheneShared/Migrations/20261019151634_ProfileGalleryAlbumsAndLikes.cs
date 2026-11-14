using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class ProfileGalleryAlbumsAndLikes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "album_id",
                table: "user_profile_gallery_images",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_profile_gallery_albums",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_profile_gallery_albums", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_profile_gallery_albums_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_profile_gallery_image_likes",
                columns: table => new
                {
                    image_id = table.Column<long>(type: "bigint", nullable: false),
                    liker_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_profile_gallery_image_likes", x => new { x.image_id, x.liker_uid });
                    table.ForeignKey(
                        name: "fk_user_profile_gallery_image_likes_user_profile_gallery_image",
                        column: x => x.image_id,
                        principalTable: "user_profile_gallery_images",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_profile_gallery_images_album_id",
                table: "user_profile_gallery_images",
                column: "album_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_profile_gallery_albums_user_uid",
                table: "user_profile_gallery_albums",
                column: "user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_user_profile_gallery_image_likes_liker_uid",
                table: "user_profile_gallery_image_likes",
                column: "liker_uid");

            migrationBuilder.AddForeignKey(
                name: "fk_user_profile_gallery_images_user_profile_gallery_albums_alb",
                table: "user_profile_gallery_images",
                column: "album_id",
                principalTable: "user_profile_gallery_albums",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_user_profile_gallery_images_user_profile_gallery_albums_alb",
                table: "user_profile_gallery_images");

            migrationBuilder.DropTable(
                name: "user_profile_gallery_albums");

            migrationBuilder.DropTable(
                name: "user_profile_gallery_image_likes");

            migrationBuilder.DropIndex(
                name: "ix_user_profile_gallery_images_album_id",
                table: "user_profile_gallery_images");

            migrationBuilder.DropColumn(
                name: "album_id",
                table: "user_profile_gallery_images");
        }
    }
}

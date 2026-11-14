using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class ProfileExtrasAndWidgetLayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "overview_layout_json",
                table: "syncshell_welcome_pages",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_profile_blog_posts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    pinned = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_profile_blog_posts", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_profile_blog_posts_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_profile_gallery_images",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    image_base64 = table.Column<string>(type: "text", nullable: true),
                    file_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    content_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    size = table.Column<long>(type: "bigint", nullable: true),
                    caption = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_profile_gallery_images", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_profile_gallery_images_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_profile_blog_posts_user_uid",
                table: "user_profile_blog_posts",
                column: "user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_user_profile_gallery_images_user_uid",
                table: "user_profile_gallery_images",
                column: "user_uid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_profile_blog_posts");

            migrationBuilder.DropTable(
                name: "user_profile_gallery_images");

            migrationBuilder.DropColumn(
                name: "overview_layout_json",
                table: "syncshell_welcome_pages");
        }
    }
}

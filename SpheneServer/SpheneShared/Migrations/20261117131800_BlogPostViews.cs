using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class BlogPostViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "view_count",
                table: "user_profile_blog_posts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "user_profile_blog_post_views",
                columns: table => new
                {
                    post_id = table.Column<long>(type: "bigint", nullable: false),
                    viewer_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    viewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_profile_blog_post_views", x => new { x.post_id, x.viewer_uid });
                    table.ForeignKey(
                        name: "fk_user_profile_blog_post_views_user_profile_blog_posts_post_id",
                        column: x => x.post_id,
                        principalTable: "user_profile_blog_posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_profile_blog_post_views_viewer_uid",
                table: "user_profile_blog_post_views",
                column: "viewer_uid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_profile_blog_post_views");

            migrationBuilder.DropColumn(
                name: "view_count",
                table: "user_profile_blog_posts");
        }
    }
}

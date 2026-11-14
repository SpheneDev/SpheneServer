using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPublishingPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "image_media_hash",
                table: "user_profile_gallery_images",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "banner_media_hash",
                table: "user_profile_data",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "profile_image_media_hash",
                table: "user_profile_data",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "row_image_media_hash",
                table: "user_profile_data",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "co_author_uids_json",
                table: "user_profile_blog_posts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "scheduled_for_utc",
                table: "user_profile_blog_posts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "status",
                table: "user_profile_blog_posts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "visibility_scope",
                table: "user_profile_blog_posts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "avatar_media_hash",
                table: "syncshell_welcome_pages",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "publishing_policy_json",
                table: "syncshell_welcome_pages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "welcome_media_hash",
                table: "syncshell_welcome_pages",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "author_uid",
                table: "syncshell_community_news",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "co_author_uids_json",
                table: "syncshell_community_news",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_note",
                table: "syncshell_community_news",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "reviewed_at",
                table: "syncshell_community_news",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reviewed_by_uid",
                table: "syncshell_community_news",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "scheduled_for_utc",
                table: "syncshell_community_news",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "status",
                table: "syncshell_community_news",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "image_media_hash",
                table: "syncshell_community_gallery",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "image_media_hash",
                table: "syncshell_community_events",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "community_media",
                columns: table => new
                {
                    hash = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    uploader_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    ref_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_community_media", x => x.hash);
                    table.ForeignKey(
                        name: "fk_community_media_users_uploader_uid",
                        column: x => x.uploader_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_community_media_uploader_uid",
                table: "community_media",
                column: "uploader_uid");

            // Backfill: the status column defaults to Draft(0) but every row
            // that existed before this migration was live content - map the
            // legacy IsDraft flag onto the new status so nothing disappears.
            migrationBuilder.Sql(
                "UPDATE syncshell_community_news SET status = CASE WHEN is_draft THEN 0 ELSE 3 END");
            migrationBuilder.Sql(
                "UPDATE user_profile_blog_posts SET status = 3");
            // Pre-existing news rows have no recorded author - the shell
            // owner is the least-wrong attribution for moderation history.
            migrationBuilder.Sql(
                "UPDATE syncshell_community_news n SET author_uid = g.owner_uid FROM groups g WHERE g.gid = n.group_gid AND n.author_uid IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "community_media");

            migrationBuilder.DropColumn(
                name: "image_media_hash",
                table: "user_profile_gallery_images");

            migrationBuilder.DropColumn(
                name: "banner_media_hash",
                table: "user_profile_data");

            migrationBuilder.DropColumn(
                name: "profile_image_media_hash",
                table: "user_profile_data");

            migrationBuilder.DropColumn(
                name: "row_image_media_hash",
                table: "user_profile_data");

            migrationBuilder.DropColumn(
                name: "co_author_uids_json",
                table: "user_profile_blog_posts");

            migrationBuilder.DropColumn(
                name: "scheduled_for_utc",
                table: "user_profile_blog_posts");

            migrationBuilder.DropColumn(
                name: "status",
                table: "user_profile_blog_posts");

            migrationBuilder.DropColumn(
                name: "visibility_scope",
                table: "user_profile_blog_posts");

            migrationBuilder.DropColumn(
                name: "avatar_media_hash",
                table: "syncshell_welcome_pages");

            migrationBuilder.DropColumn(
                name: "publishing_policy_json",
                table: "syncshell_welcome_pages");

            migrationBuilder.DropColumn(
                name: "welcome_media_hash",
                table: "syncshell_welcome_pages");

            migrationBuilder.DropColumn(
                name: "author_uid",
                table: "syncshell_community_news");

            migrationBuilder.DropColumn(
                name: "co_author_uids_json",
                table: "syncshell_community_news");

            migrationBuilder.DropColumn(
                name: "review_note",
                table: "syncshell_community_news");

            migrationBuilder.DropColumn(
                name: "reviewed_at",
                table: "syncshell_community_news");

            migrationBuilder.DropColumn(
                name: "reviewed_by_uid",
                table: "syncshell_community_news");

            migrationBuilder.DropColumn(
                name: "scheduled_for_utc",
                table: "syncshell_community_news");

            migrationBuilder.DropColumn(
                name: "status",
                table: "syncshell_community_news");

            migrationBuilder.DropColumn(
                name: "image_media_hash",
                table: "syncshell_community_gallery");

            migrationBuilder.DropColumn(
                name: "image_media_hash",
                table: "syncshell_community_events");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddCommentsReactionsModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "comment_count",
                table: "user_profile_blog_posts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "comments_locked",
                table: "user_profile_blog_posts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "comment_count",
                table: "syncshell_community_news",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "comments_locked",
                table: "syncshell_community_news",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "comment_count",
                table: "syncshell_community_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "comments_locked",
                table: "syncshell_community_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "community_comments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    target_kind = table.Column<int>(type: "integer", nullable: false),
                    target_id = table.Column<long>(type: "bigint", nullable: false),
                    group_gid = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    owner_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    author_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    parent_id = table.Column<long>(type: "bigint", nullable: true),
                    root_id = table.Column<long>(type: "bigint", nullable: true),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    state = table.Column<int>(type: "integer", nullable: false),
                    reply_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    edited_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    moderated_by_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    moderated_by_scope = table.Column<int>(type: "integer", nullable: false),
                    moderation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    moderated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_community_comments", x => x.id);
                    table.CheckConstraint("ck_community_comments_root", "(parent_id IS NULL) = (root_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_community_comments_community_comments_parent_id",
                        column: x => x.parent_id,
                        principalTable: "community_comments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_community_comments_community_comments_root_id",
                        column: x => x.root_id,
                        principalTable: "community_comments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_community_comments_groups_group_gid",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_community_comments_users_author_uid",
                        column: x => x.author_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_community_comments_users_owner_uid",
                        column: x => x.owner_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "community_moderation_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    actor_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor_scope = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    target_kind = table.Column<int>(type: "integer", nullable: false),
                    target_id = table.Column<long>(type: "bigint", nullable: false),
                    comment_id = table.Column<long>(type: "bigint", nullable: true),
                    report_id = table.Column<long>(type: "bigint", nullable: true),
                    group_gid = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    owner_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_community_moderation_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "community_reactions",
                columns: table => new
                {
                    target_kind = table.Column<int>(type: "integer", nullable: false),
                    target_id = table.Column<long>(type: "bigint", nullable: false),
                    user_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    group_gid = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    owner_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_community_reactions", x => new { x.target_kind, x.target_id, x.user_uid, x.kind });
                    table.ForeignKey(
                        name: "fk_community_reactions_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "community_reports",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    target_kind = table.Column<int>(type: "integer", nullable: false),
                    target_id = table.Column<long>(type: "bigint", nullable: false),
                    group_gid = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    owner_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reporter_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<int>(type: "integer", nullable: false),
                    details = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    state = table.Column<int>(type: "integer", nullable: false),
                    resolved_by_uid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_community_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_community_reports_groups_group_gid",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_community_reports_users_owner_uid",
                        column: x => x.owner_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_community_reports_users_reporter_uid",
                        column: x => x.reporter_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_community_comments_author_uid",
                table: "community_comments",
                column: "author_uid");

            migrationBuilder.CreateIndex(
                name: "ix_community_comments_group_gid",
                table: "community_comments",
                column: "group_gid");

            migrationBuilder.CreateIndex(
                name: "ix_community_comments_owner_uid",
                table: "community_comments",
                column: "owner_uid");

            migrationBuilder.CreateIndex(
                name: "ix_community_comments_parent_id",
                table: "community_comments",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_community_comments_root_id_id",
                table: "community_comments",
                columns: new[] { "root_id", "id" },
                filter: "root_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_community_comments_target_kind_target_id_state_id",
                table: "community_comments",
                columns: new[] { "target_kind", "target_id", "state", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_community_moderation_log_actor_uid",
                table: "community_moderation_log",
                column: "actor_uid");

            migrationBuilder.CreateIndex(
                name: "ix_community_moderation_log_group_gid",
                table: "community_moderation_log",
                column: "group_gid");

            migrationBuilder.CreateIndex(
                name: "ix_community_moderation_log_target_kind_target_id",
                table: "community_moderation_log",
                columns: new[] { "target_kind", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ix_community_reactions_group_gid",
                table: "community_reactions",
                column: "group_gid");

            migrationBuilder.CreateIndex(
                name: "ix_community_reactions_target_kind_target_id_kind",
                table: "community_reactions",
                columns: new[] { "target_kind", "target_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_community_reactions_user_uid",
                table: "community_reactions",
                column: "user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_community_reports_group_gid_state",
                table: "community_reports",
                columns: new[] { "group_gid", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_community_reports_owner_uid_state",
                table: "community_reports",
                columns: new[] { "owner_uid", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_community_reports_reporter_uid_target_kind_target_id",
                table: "community_reports",
                columns: new[] { "reporter_uid", "target_kind", "target_id" },
                unique: true,
                filter: "state = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "community_comments");

            migrationBuilder.DropTable(
                name: "community_moderation_log");

            migrationBuilder.DropTable(
                name: "community_reactions");

            migrationBuilder.DropTable(
                name: "community_reports");

            migrationBuilder.DropColumn(
                name: "comment_count",
                table: "user_profile_blog_posts");

            migrationBuilder.DropColumn(
                name: "comments_locked",
                table: "user_profile_blog_posts");

            migrationBuilder.DropColumn(
                name: "comment_count",
                table: "syncshell_community_news");

            migrationBuilder.DropColumn(
                name: "comments_locked",
                table: "syncshell_community_news");

            migrationBuilder.DropColumn(
                name: "comment_count",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "comments_locked",
                table: "syncshell_community_events");
        }
    }
}

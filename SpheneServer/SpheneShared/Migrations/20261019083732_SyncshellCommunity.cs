using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class SyncshellCommunity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_listed_publicly",
                table: "syncshell_welcome_pages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "links_json",
                table: "syncshell_welcome_pages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rules_text",
                table: "syncshell_welcome_pages",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "visible_sections_json",
                table: "syncshell_welcome_pages",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "syncshell_community_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    group_gid = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    end_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    image_base64 = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_syncshell_community_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_syncshell_community_events_groups_group_gid",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "syncshell_community_gallery",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    group_gid = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    image_base64 = table.Column<string>(type: "text", nullable: true),
                    file_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    content_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    size = table.Column<long>(type: "bigint", nullable: true),
                    caption = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_syncshell_community_gallery", x => x.id);
                    table.ForeignKey(
                        name: "fk_syncshell_community_gallery_groups_group_gid",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "syncshell_community_news",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    group_gid = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    is_pinned = table.Column<bool>(type: "boolean", nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_syncshell_community_news", x => x.id);
                    table.ForeignKey(
                        name: "fk_syncshell_community_news_groups_group_gid",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_welcome_pages_is_listed_publicly",
                table: "syncshell_welcome_pages",
                column: "is_listed_publicly");

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_community_events_group_gid",
                table: "syncshell_community_events",
                column: "group_gid");

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_community_events_start_utc",
                table: "syncshell_community_events",
                column: "start_utc");

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_community_gallery_group_gid",
                table: "syncshell_community_gallery",
                column: "group_gid");

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_community_news_group_gid",
                table: "syncshell_community_news",
                column: "group_gid");

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_community_news_published_at",
                table: "syncshell_community_news",
                column: "published_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "syncshell_community_events");

            migrationBuilder.DropTable(
                name: "syncshell_community_gallery");

            migrationBuilder.DropTable(
                name: "syncshell_community_news");

            migrationBuilder.DropIndex(
                name: "ix_syncshell_welcome_pages_is_listed_publicly",
                table: "syncshell_welcome_pages");

            migrationBuilder.DropColumn(
                name: "is_listed_publicly",
                table: "syncshell_welcome_pages");

            migrationBuilder.DropColumn(
                name: "links_json",
                table: "syncshell_welcome_pages");

            migrationBuilder.DropColumn(
                name: "rules_text",
                table: "syncshell_welcome_pages");

            migrationBuilder.DropColumn(
                name: "visible_sections_json",
                table: "syncshell_welcome_pages");
        }
    }
}

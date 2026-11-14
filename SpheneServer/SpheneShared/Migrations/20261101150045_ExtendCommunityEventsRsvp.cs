using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class ExtendCommunityEventsRsvp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attending_count",
                table: "syncshell_community_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "author_uid",
                table: "syncshell_community_events",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "event_status",
                table: "syncshell_community_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "max_attendees",
                table: "syncshell_community_events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "organizer_uids_json",
                table: "syncshell_community_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "publish_status",
                table: "syncshell_community_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "recurrence_rule",
                table: "syncshell_community_events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reminders_json",
                table: "syncshell_community_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_note",
                table: "syncshell_community_events",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "reviewed_at",
                table: "syncshell_community_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reviewed_by_uid",
                table: "syncshell_community_events",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "rsvp_version",
                table: "syncshell_community_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "timezone_id",
                table: "syncshell_community_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "visibility",
                table: "syncshell_community_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "waitlist_count",
                table: "syncshell_community_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "community_event_exceptions",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false),
                    occurrence_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    end_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_community_event_exceptions", x => new { x.event_id, x.occurrence_start_utc });
                    table.ForeignKey(
                        name: "fk_community_event_exceptions_syncshell_community_events_event",
                        column: x => x.event_id,
                        principalTable: "syncshell_community_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "community_event_participants",
                columns: table => new
                {
                    event_id = table.Column<long>(type: "bigint", nullable: false),
                    user_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    waitlist_position = table.Column<int>(type: "integer", nullable: true),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    promoted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_community_event_participants", x => new { x.event_id, x.user_uid });
                    table.ForeignKey(
                        name: "fk_community_event_participants_syncshell_community_events_eve",
                        column: x => x.event_id,
                        principalTable: "syncshell_community_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_community_event_participants_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_community_event_participants_event_id_status",
                table: "community_event_participants",
                columns: new[] { "event_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_community_event_participants_user_uid",
                table: "community_event_participants",
                column: "user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_community_event_participants_waitlist_position",
                table: "community_event_participants",
                column: "waitlist_position",
                filter: "waitlist_position IS NOT NULL");

            // Existing events are live today - keep them published and
            // attribute them to the shell owner (same convention as news).
            migrationBuilder.Sql(
                "UPDATE syncshell_community_events SET publish_status = 3, " +
                "author_uid = (SELECT owner_uid FROM groups WHERE groups.gid = syncshell_community_events.group_gid)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "community_event_exceptions");

            migrationBuilder.DropTable(
                name: "community_event_participants");

            migrationBuilder.DropColumn(
                name: "attending_count",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "author_uid",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "event_status",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "max_attendees",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "organizer_uids_json",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "publish_status",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "recurrence_rule",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "reminders_json",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "review_note",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "reviewed_at",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "reviewed_by_uid",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "rsvp_version",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "timezone_id",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "visibility",
                table: "syncshell_community_events");

            migrationBuilder.DropColumn(
                name: "waitlist_count",
                table: "syncshell_community_events");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddUserNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "notification_disabled_kinds",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "user_notifications",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    recipient_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    actor_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    group_gid = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    content_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    content_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    snippet = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_read = table.Column<bool>(type: "boolean", nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_notifications_users_recipient_uid",
                        column: x => x.recipient_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_notifications_recipient_uid",
                table: "user_notifications",
                column: "recipient_uid");

            migrationBuilder.CreateIndex(
                name: "ix_user_notifications_recipient_uid_dedupe_key",
                table: "user_notifications",
                columns: new[] { "recipient_uid", "dedupe_key" },
                unique: true,
                filter: "dedupe_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_user_notifications_recipient_uid_is_read",
                table: "user_notifications",
                columns: new[] { "recipient_uid", "is_read" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_notifications");

            migrationBuilder.DropColumn(
                name: "notification_disabled_kinds",
                table: "users");
        }
    }
}

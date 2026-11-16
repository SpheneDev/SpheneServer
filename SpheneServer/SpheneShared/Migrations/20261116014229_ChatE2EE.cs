using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class ChatE2EE : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "kind",
                table: "chat_messages",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<DateTime>(
                name: "migrated_at_utc",
                table: "chat_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "migrated_by_uid",
                table: "chat_messages",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "chat_device_keys",
                columns: table => new
                {
                    user_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    device_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    public_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    published_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_device_keys", x => new { x.user_uid, x.device_id });
                    table.ForeignKey(
                        name: "fk_chat_device_keys_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chat_message_ciphers",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    for_device_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    for_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    sealer_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    sealer_device_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sealer_public_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    blob = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_message_ciphers", x => new { x.message_id, x.for_device_id });
                    table.ForeignKey(
                        name: "fk_chat_message_ciphers_chat_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "chat_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chat_message_ciphers_for_uid",
                table: "chat_message_ciphers",
                column: "for_uid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_device_keys");

            migrationBuilder.DropTable(
                name: "chat_message_ciphers");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "migrated_at_utc",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "migrated_by_uid",
                table: "chat_messages");
        }
    }
}

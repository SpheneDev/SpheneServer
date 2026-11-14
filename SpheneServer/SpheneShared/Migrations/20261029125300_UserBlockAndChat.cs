using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class UserBlockAndChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_uid = table.Column<string>(type: "character varying(10)", nullable: true),
                    recipient_uid = table.Column<string>(type: "character varying(10)", nullable: true),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_chat_messages_users_recipient_uid",
                        column: x => x.recipient_uid,
                        principalTable: "users",
                        principalColumn: "uid");
                    table.ForeignKey(
                        name: "fk_chat_messages_users_sender_uid",
                        column: x => x.sender_uid,
                        principalTable: "users",
                        principalColumn: "uid");
                });

            migrationBuilder.CreateTable(
                name: "user_blocks",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_uid = table.Column<string>(type: "character varying(10)", nullable: true),
                    blocked_user_uid = table.Column<string>(type: "character varying(10)", nullable: true),
                    blocked_on = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_blocks", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_blocks_users_blocked_user_uid",
                        column: x => x.blocked_user_uid,
                        principalTable: "users",
                        principalColumn: "uid");
                    table.ForeignKey(
                        name: "fk_user_blocks_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid");
                });

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_recipient_uid",
                table: "chat_messages",
                column: "recipient_uid");

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_sender_uid",
                table: "chat_messages",
                column: "sender_uid");

            migrationBuilder.CreateIndex(
                name: "ix_user_blocks_blocked_user_uid",
                table: "user_blocks",
                column: "blocked_user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_user_blocks_user_uid",
                table: "user_blocks",
                column: "user_uid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_messages");

            migrationBuilder.DropTable(
                name: "user_blocks");
        }
    }
}

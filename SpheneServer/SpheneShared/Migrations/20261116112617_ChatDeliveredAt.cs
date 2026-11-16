using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class ChatDeliveredAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "delivered_at",
                table: "chat_messages",
                type: "timestamp with time zone",
                nullable: true);

            // Read implies delivered - backfill so pre-existing read rows
            // show "read" instead of a misleading "sent" state.
            migrationBuilder.Sql(
                "UPDATE chat_messages SET delivered_at = read_at WHERE read_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "delivered_at",
                table: "chat_messages");
        }
    }
}

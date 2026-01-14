using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddModShareHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mod_share_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    sender_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    recipient_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    hash = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    shared_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_share_history", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mod_share_history_hash",
                table: "mod_share_history",
                column: "hash");

            migrationBuilder.CreateIndex(
                name: "ix_mod_share_history_recipient_uid",
                table: "mod_share_history",
                column: "recipient_uid");

            migrationBuilder.CreateIndex(
                name: "ix_mod_share_history_sender_uid",
                table: "mod_share_history",
                column: "sender_uid");

            migrationBuilder.CreateIndex(
                name: "ix_mod_share_history_shared_at",
                table: "mod_share_history",
                column: "shared_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mod_share_history");
        }
    }
}

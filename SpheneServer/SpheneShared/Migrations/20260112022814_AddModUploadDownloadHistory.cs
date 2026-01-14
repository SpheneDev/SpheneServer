using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddModUploadDownloadHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mod_download_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    hash = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    downloaded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_download_history", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mod_download_history_downloaded_at",
                table: "mod_download_history",
                column: "downloaded_at");

            migrationBuilder.CreateIndex(
                name: "ix_mod_download_history_hash",
                table: "mod_download_history",
                column: "hash");

            migrationBuilder.CreateIndex(
                name: "ix_mod_download_history_user_uid",
                table: "mod_download_history",
                column: "user_uid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mod_download_history");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class SyncshellFavorites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "syncshell_favorites",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    group_gid = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    user_uid = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_syncshell_favorites", x => x.id);
                    table.ForeignKey(
                        name: "fk_syncshell_favorites_groups_group_gid",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid");
                });

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_favorites_group_gid",
                table: "syncshell_favorites",
                column: "group_gid");

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_favorites_user_uid_group_gid",
                table: "syncshell_favorites",
                columns: new[] { "user_uid", "group_gid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "syncshell_favorites");
        }
    }
}

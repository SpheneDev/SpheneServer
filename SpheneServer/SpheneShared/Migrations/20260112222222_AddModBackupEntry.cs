using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Sphene.API.Dto.Files;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddModBackupEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "penumbra_mod_backups",
                columns: table => new
                {
                    backup_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    backup_name = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_complete = table.Column<bool>(type: "boolean", nullable: false),
                    mod_count = table.Column<int>(type: "integer", nullable: false),
                    mods = table.Column<List<PenumbraModBackupEntryDto>>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_penumbra_mod_backups", x => x.backup_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_penumbra_mod_backups_created_at",
                table: "penumbra_mod_backups",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_penumbra_mod_backups_user_uid",
                table: "penumbra_mod_backups",
                column: "user_uid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "penumbra_mod_backups");
        }
    }
}

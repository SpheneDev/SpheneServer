using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Sphene.API.Dto.Files;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingFileTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_file_transfers",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    recipient_uid = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sender_uid = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sender_alias = table.Column<string>(type: "text", nullable: true),
                    hash = table.Column<string>(type: "text", nullable: false),
                    mod_folder_name = table.Column<string>(type: "text", nullable: true),
                    mod_info = table.Column<List<ModInfoDto>>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_file_transfers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pending_file_transfers_recipient_uid",
                table: "pending_file_transfers",
                column: "recipient_uid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_file_transfers");
        }
    }
}

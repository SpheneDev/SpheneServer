using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddCharaDataHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chara_data_hashes",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    parent_uploader_uid = table.Column<string>(type: "character varying(10)", nullable: true),
                    parent_id = table.Column<string>(type: "text", nullable: true),
                    hash = table.Column<string>(type: "text", nullable: true),
                    last_updated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chara_data_hashes", x => x.id);
                    table.ForeignKey(
                        name: "fk_chara_data_hashes_chara_data_parent_id_parent_uploader_uid",
                        columns: x => new { x.parent_id, x.parent_uploader_uid },
                        principalTable: "chara_data",
                        principalColumns: new[] { "id", "uploader_uid" });
                });

            migrationBuilder.CreateIndex(
                name: "ix_chara_data_hashes_parent_id",
                table: "chara_data_hashes",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_chara_data_hashes_parent_id_parent_uploader_uid",
                table: "chara_data_hashes",
                columns: new[] { "parent_id", "parent_uploader_uid" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chara_data_hashes");
        }
    }
}

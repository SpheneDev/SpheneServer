using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddAreaBoundSyncshellConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "join_rules",
                table: "area_bound_syncshells",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "require_rules_acceptance",
                table: "area_bound_syncshells",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "rules_version",
                table: "area_bound_syncshells",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "area_bound_syncshell_consents",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_uid = table.Column<string>(type: "character varying(10)", nullable: true),
                    syncshell_gid = table.Column<string>(type: "character varying(20)", nullable: true),
                    has_accepted = table.Column<bool>(type: "boolean", nullable: false),
                    consent_given_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_rules_accepted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    accepted_rules_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_area_bound_syncshell_consents", x => x.id);
                    table.ForeignKey(
                        name: "fk_area_bound_syncshell_consents_groups_syncshell_gid",
                        column: x => x.syncshell_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_area_bound_syncshell_consents_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_area_bound_syncshell_consents_syncshell_gid",
                table: "area_bound_syncshell_consents",
                column: "syncshell_gid");

            migrationBuilder.CreateIndex(
                name: "ix_area_bound_syncshell_consents_user_uid_syncshell_gid",
                table: "area_bound_syncshell_consents",
                columns: new[] { "user_uid", "syncshell_gid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "area_bound_syncshell_consents");

            migrationBuilder.DropColumn(
                name: "join_rules",
                table: "area_bound_syncshells");

            migrationBuilder.DropColumn(
                name: "require_rules_acceptance",
                table: "area_bound_syncshells");

            migrationBuilder.DropColumn(
                name: "rules_version",
                table: "area_bound_syncshells");
        }
    }
}

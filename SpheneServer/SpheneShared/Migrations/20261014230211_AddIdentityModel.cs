using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "alias_changed_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "display_name",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Empty-string aliases are "no alias" - normalize before the unique index.
            migrationBuilder.Sql("""
                UPDATE users SET alias = NULL WHERE alias = '';
                """);

            // Existing duplicates (case-insensitive): keep the alphabetically-first uid's alias,
            // clear the rest so the partial unique index can be built.
            migrationBuilder.Sql("""
                UPDATE users u SET alias = NULL
                WHERE u.alias IS NOT NULL AND u.uid <> (
                    SELECT DISTINCT ON (lower(u2.alias)) u2.uid
                    FROM users u2 WHERE u2.alias IS NOT NULL AND lower(u2.alias) = lower(u.alias)
                    ORDER BY lower(u2.alias), u2.uid);
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_users_alias_lower ON users (lower(alias)) WHERE alias IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS ux_users_alias_lower;""");

            migrationBuilder.DropColumn(
                name: "alias_changed_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "display_name",
                table: "users");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class SyncshellCommunityStaffRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "syncshell_community_staff_assignments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    group_gid = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    user_uid = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    role_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_syncshell_community_staff_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_syncshell_community_staff_assignments_groups_group_gid",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "syncshell_community_staff_roles",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    group_gid = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    color_argb = table.Column<long>(type: "bigint", nullable: false),
                    icon = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    permissions = table.Column<long>(type: "bigint", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_syncshell_community_staff_roles", x => x.id);
                    table.ForeignKey(
                        name: "fk_syncshell_community_staff_roles_groups_group_gid",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_community_staff_assignments_group_gid_user_uid",
                table: "syncshell_community_staff_assignments",
                columns: new[] { "group_gid", "user_uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_community_staff_assignments_role_id",
                table: "syncshell_community_staff_assignments",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_community_staff_roles_group_gid",
                table: "syncshell_community_staff_roles",
                column: "group_gid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "syncshell_community_staff_assignments");

            migrationBuilder.DropTable(
                name: "syncshell_community_staff_roles");
        }
    }
}

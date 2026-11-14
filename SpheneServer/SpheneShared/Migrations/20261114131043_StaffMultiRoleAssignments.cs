using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class StaffMultiRoleAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_syncshell_community_staff_assignments_group_gid_user_uid",
                table: "syncshell_community_staff_assignments");

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_community_staff_assignments_group_gid_user_uid_ro",
                table: "syncshell_community_staff_assignments",
                columns: new[] { "group_gid", "user_uid", "role_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_syncshell_community_staff_assignments_group_gid_user_uid_ro",
                table: "syncshell_community_staff_assignments");

            migrationBuilder.CreateIndex(
                name: "ix_syncshell_community_staff_assignments_group_gid_user_uid",
                table: "syncshell_community_staff_assignments",
                columns: new[] { "group_gid", "user_uid" },
                unique: true);
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipApplicationsAndJoinPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "join_policy",
                table: "groups",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "group_membership_applications",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    group_gid = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    applicant_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reviewed_by_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    review_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_membership_applications", x => x.id);
                    table.ForeignKey(
                        name: "fk_group_membership_applications_groups_group_gid",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_group_membership_applications_users_applicant_uid",
                        column: x => x.applicant_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_group_membership_applications_users_reviewed_by_uid",
                        column: x => x.reviewed_by_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_group_membership_applications_applicant_uid",
                table: "group_membership_applications",
                column: "applicant_uid");

            migrationBuilder.CreateIndex(
                name: "ix_group_membership_applications_group_gid_applicant_uid",
                table: "group_membership_applications",
                columns: new[] { "group_gid", "applicant_uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_group_membership_applications_group_gid_status",
                table: "group_membership_applications",
                columns: new[] { "group_gid", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_group_membership_applications_reviewed_by_uid",
                table: "group_membership_applications",
                column: "reviewed_by_uid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_membership_applications");

            migrationBuilder.DropColumn(
                name: "join_policy",
                table: "groups");
        }
    }
}

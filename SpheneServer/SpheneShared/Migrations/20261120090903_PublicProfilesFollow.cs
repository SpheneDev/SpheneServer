using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class PublicProfilesFollow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "public_profile_opt_in",
                table: "user_profile_data",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "user_follows",
                columns: table => new
                {
                    follower_uid = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_uid = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    followed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_follows", x => new { x.follower_uid, x.target_uid });
                    table.ForeignKey(
                        name: "fk_user_follows_users_follower_uid",
                        column: x => x.follower_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_follows_users_target_uid",
                        column: x => x.target_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_follows_target_uid",
                table: "user_follows",
                column: "target_uid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_follows");

            migrationBuilder.DropColumn(
                name: "public_profile_opt_in",
                table: "user_profile_data");
        }
    }
}

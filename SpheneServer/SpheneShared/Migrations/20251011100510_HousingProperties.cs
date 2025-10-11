using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class HousingProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_housing_properties",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    server_id = table.Column<long>(type: "bigint", nullable: false),
                    map_id = table.Column<long>(type: "bigint", nullable: false),
                    territory_id = table.Column<long>(type: "bigint", nullable: false),
                    division_id = table.Column<long>(type: "bigint", nullable: false),
                    ward_id = table.Column<long>(type: "bigint", nullable: false),
                    house_id = table.Column<long>(type: "bigint", nullable: false),
                    room_id = table.Column<long>(type: "bigint", nullable: false),
                    is_indoor = table.Column<bool>(type: "boolean", nullable: false),
                    allow_outdoor = table.Column<bool>(type: "boolean", nullable: false),
                    allow_indoor = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_housing_properties", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_housing_properties_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_housing_properties_user_uid",
                table: "user_housing_properties",
                column: "user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_user_housing_properties_user_uid_server_id_territory_id_war",
                table: "user_housing_properties",
                columns: new[] { "user_uid", "server_id", "territory_id", "ward_id", "house_id", "room_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_housing_properties");
        }
    }
}

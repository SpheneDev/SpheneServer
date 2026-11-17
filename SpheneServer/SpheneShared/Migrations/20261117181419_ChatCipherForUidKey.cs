using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class ChatCipherForUidKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_chat_message_ciphers",
                table: "chat_message_ciphers");

            migrationBuilder.AlterColumn<string>(
                name: "for_uid",
                table: "chat_message_ciphers",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10,
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_chat_message_ciphers",
                table: "chat_message_ciphers",
                columns: new[] { "message_id", "for_uid", "for_device_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_chat_message_ciphers",
                table: "chat_message_ciphers");

            migrationBuilder.AlterColumn<string>(
                name: "for_uid",
                table: "chat_message_ciphers",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.AddPrimaryKey(
                name: "pk_chat_message_ciphers",
                table: "chat_message_ciphers",
                columns: new[] { "message_id", "for_device_id" });
        }
    }
}

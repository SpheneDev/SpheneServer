using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SpheneServer.Migrations
{
    /// <inheritdoc />
    public partial class ReactionEmojiSeq : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_community_reactions",
                table: "community_reactions");

            migrationBuilder.DropIndex(
                name: "ix_community_reactions_target_kind_target_id_kind",
                table: "community_reactions");

            migrationBuilder.AddColumn<string>(
                name: "emoji_seq",
                table: "community_reactions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");


            // Backfill legacy int-kind rows to their canonical chat-emoji
            // glyphs BEFORE the new PK applies - the kind-per-user uniqueness
            // carries over because the kind→glyph map is injective.
            migrationBuilder.Sql("""
                UPDATE community_reactions
                SET emoji_seq = CASE kind
                    WHEN 0 THEN '👍'
                    WHEN 1 THEN '❤'
                    WHEN 2 THEN '😂'
                    WHEN 3 THEN '😮'
                    WHEN 4 THEN '😢'
                    ELSE '👍' END
                WHERE emoji_seq = '';
                """);

            // Merged duplicates are impossible per the injective map, but a
            // defensive sweep keeps the PK creation below fail-safe.
            migrationBuilder.Sql("""
                DELETE FROM community_reactions a
                USING community_reactions b
                WHERE a.target_kind = b.target_kind
                  AND a.target_id = b.target_id
                  AND a.user_uid = b.user_uid
                  AND a.emoji_seq = b.emoji_seq
                  AND a.created_at > b.created_at;
                """);

            migrationBuilder.AddPrimaryKey(
                name: "pk_community_reactions",
                table: "community_reactions",
                columns: new[] { "target_kind", "target_id", "user_uid", "emoji_seq" });

            migrationBuilder.CreateIndex(
                name: "ix_community_reactions_target_kind_target_id_emoji_seq",
                table: "community_reactions",
                columns: new[] { "target_kind", "target_id", "emoji_seq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_community_reactions",
                table: "community_reactions");

            migrationBuilder.DropIndex(
                name: "ix_community_reactions_target_kind_target_id_emoji_seq",
                table: "community_reactions");

            migrationBuilder.DropColumn(
                name: "emoji_seq",
                table: "community_reactions");

            migrationBuilder.AddPrimaryKey(
                name: "pk_community_reactions",
                table: "community_reactions",
                columns: new[] { "target_kind", "target_id", "user_uid", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_community_reactions_target_kind_target_id_kind",
                table: "community_reactions",
                columns: new[] { "target_kind", "target_id", "kind" });
        }
    }
}

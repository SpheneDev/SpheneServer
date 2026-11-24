using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

/// <summary>One user's reaction on a target. Composite PK
/// (target_kind, target_id, user_uid, emoji_seq) is the uniqueness authority -
/// SetReaction = INSERT ON CONFLICT DO NOTHING, RemoveReaction = DELETE;
/// both idempotent under concurrent requests. The reaction identity is the
/// emoji glyph; legacy int-kind rows were backfilled to their canonical
/// glyphs by the ReactionEmojiSeq migration.</summary>
public class CommunityReaction
{
    /// <summary><see cref="Sphene.API.Data.Enum.ReactionTargetKind"/> as int.</summary>
    public int TargetKind { get; set; }
    /// <summary>For Comment targets this is the COMMENT id (the reaction's
    /// immediate target), not the host post id.</summary>
    public long TargetId { get; set; }

    [MaxLength(64)]
    public string UserUID { get; set; } = string.Empty;
    public User? User { get; set; }

    /// <summary>The reacted emoji glyph (UTF-8, e.g. a chat-picker pick) -
    /// the reaction's identity. Never empty post-migration.</summary>
    [MaxLength(32)]
    public string EmojiSeq { get; set; } = string.Empty;

    /// <summary>Legacy <see cref="Sphene.API.Data.Enum.ReactionKind"/> as
    /// int - frozen on migrated rows, reverse-mapped or -1 on new writes.
    /// Kept for schema compat only; new reads key on <see cref="EmojiSeq"/>.</summary>
    public int Kind { get; set; }

    /// <summary>Denormalized shell scope - cascade + fast group-scoped reads.</summary>
    [MaxLength(40)]
    public string? GroupGID { get; set; }
    /// <summary>Denormalized blog-owner scope - cascade + ACL reads.</summary>
    [MaxLength(64)]
    public string? OwnerUid { get; set; }

    /// <summary>Insert instant - ORDER BY for the who-reacted listing.</summary>
    public DateTime CreatedAt { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

/// <summary>One user's reaction on a target. Composite PK
/// (target_kind, target_id, user_uid, kind) is the uniqueness authority -
/// SetReaction = INSERT ON CONFLICT DO NOTHING, RemoveReaction = DELETE;
/// both idempotent under concurrent requests.</summary>
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

    /// <summary><see cref="Sphene.API.Data.Enum.ReactionKind"/> as int.</summary>
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

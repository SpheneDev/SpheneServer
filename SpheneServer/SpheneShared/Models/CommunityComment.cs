using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SpheneShared.Models;

/// <summary>One comment on community news / events / personal blog posts.
/// Threading: depth-2 - roots have <see cref="ParentId"/>==null AND
/// <see cref="RootId"/>==null; replies carry both (root_id always points at the
/// depth-0 ancestor, enabling flat keyset paging per thread).</summary>
public class CommunityComment
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary><see cref="Sphene.API.Data.Enum.CommentTargetKind"/> as int.</summary>
    public int TargetKind { get; set; }
    public long TargetId { get; set; }

    /// <summary>Shell scope for community targets - real FK for cascades.</summary>
    [MaxLength(40)]
    public string? GroupGID { get; set; }
    public Group? Group { get; set; }

    /// <summary>Profile-owner scope for blog targets - real FK for cascades.</summary>
    [MaxLength(64)]
    public string? OwnerUid { get; set; }
    public User? Owner { get; set; }

    [Required]
    [MaxLength(64)]
    public string AuthorUid { get; set; } = string.Empty;
    public User Author { get; set; } = null!;

    /// <summary>Reply parent - null on roots. Self-FK, Restrict: comments are
    /// never hard-deleted (tombstones), so no cascade needed.</summary>
    public long? ParentId { get; set; }
    public CommunityComment? Parent { get; set; }

    /// <summary>Depth-0 ancestor id - null on roots by design (avoids the
    /// chicken-and-egg of assigning a generated id before insert).</summary>
    public long? RootId { get; set; }
    public CommunityComment? Root { get; set; }

    [MaxLength(4000)]
    public string Body { get; set; } = string.Empty;

    /// <summary><see cref="Sphene.API.Data.Enum.CommentState"/> as int.</summary>
    public int State { get; set; }

    /// <summary>Direct reply count - maintained inside the parent row lock.</summary>
    public int ReplyCount { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? EditedAt { get; set; }

    /// <summary>UID of whoever last acted on this comment's state - author,
    /// staff, blog owner or platform mod.</summary>
    [MaxLength(64)]
    public string? ModeratedByUid { get; set; }
    /// <summary>0 none · 1 blog owner · 2 community staff · 3 platform.
    /// Distinguishes owner-hides from platform-hides for the restore rules -
    /// the state enum alone cannot.</summary>
    public int ModeratedByScope { get; set; }
    [MaxLength(500)]
    public string? ModerationReason { get; set; }
    public DateTime? ModeratedAt { get; set; }
}

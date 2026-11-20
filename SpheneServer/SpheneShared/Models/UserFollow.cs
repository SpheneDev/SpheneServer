using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SpheneShared.Models;

/// <summary>
/// A unilateral public-profile follow edge. Grants no pair, chat, mod-sync or
/// group rights - it only feeds the caller's "Following" feed and marks the
/// followed state on the target's public profile. Survives the target turning
/// private; visibility is re-checked on every read.
/// </summary>
[Table("user_follows")]
[Index(nameof(TargetUID))]
public class UserFollow
{
    [Required]
    [MaxLength(20)]
    public string FollowerUID { get; set; } = string.Empty;
    [ForeignKey(nameof(FollowerUID))]
    public User Follower { get; set; } = null!;

    [Required]
    [MaxLength(20)]
    public string TargetUID { get; set; } = string.Empty;
    [ForeignKey(nameof(TargetUID))]
    public User Target { get; set; } = null!;

    public DateTime FollowedAtUtc { get; set; }
}

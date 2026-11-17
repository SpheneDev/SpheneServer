using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SpheneShared.Models;

/// <summary>
/// One row per (post, viewer) that ever opened a Chronicle's detail view -
/// the dedup record behind <see cref="UserProfileBlogPost.ViewCount"/>.
/// Composite key keeps each non-owner reader counted exactly once.
/// </summary>
public class UserProfileBlogPostView
{
    [Required]
    [ForeignKey(nameof(Post))]
    public long PostId { get; set; }
    public UserProfileBlogPost Post { get; set; }

    [Required]
    [MaxLength(64)]
    public string ViewerUID { get; set; }

    public DateTime ViewedAt { get; set; }
}

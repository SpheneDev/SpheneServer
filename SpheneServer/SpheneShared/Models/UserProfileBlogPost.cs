using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SpheneShared.Models;

public class UserProfileBlogPost : ICommentThreadHost
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required]
    [MaxLength(64)]
    [ForeignKey(nameof(User))]
    public string UserUID { get; set; }
    public User User { get; set; }

    [MaxLength(200)]
    public string Title { get; set; }

    [MaxLength(8000)]
    public string? Body { get; set; }

    public bool Pinned { get; set; }

    /// <summary><see cref="Sphene.API.Data.Enum.ContentPublishStatus"/> as int.</summary>
    public int Status { get; set; } = 3;
    /// <summary><see cref="Sphene.API.Data.Enum.BlogVisibilityScope"/> as int.</summary>
    public int VisibilityScope { get; set; }
    public DateTime? ScheduledForUtc { get; set; }
    /// <summary>JSON list of co-author UIDs allowed to edit this post.</summary>
    public string? CoAuthorUidsJson { get; set; }

    /// <summary>Community-media hash of the optional cover image.</summary>
    [MaxLength(128)]
    public string? CoverMediaHash { get; set; }

    /// <summary>Denormalized comment count - maintained under comment txns.</summary>
    public int CommentCount { get; set; }
    /// <summary>Owner/platform lock - comment writes rejected while set.</summary>
    public bool CommentsLocked { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

/// <summary>
/// A persisted, staff-authored announcement shown on the client's news page
/// and served to external consumers (website, Discord bot) via the public
/// REST surface. <see cref="Body"/> is the canonical CommonMark-subset
/// markdown; the hero image lives in <see cref="BannerImageUrl"/>, never
/// inline. Drafts stay staff-only until published; deletes are soft.
/// </summary>
public class Announcement
{
    [Key]
    public long Id { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Summary { get; set; } = string.Empty;

    [MaxLength(16000)]
    public string Body { get; set; } = string.Empty;

    /// <summary>news | event | maintenance | patch</summary>
    [MaxLength(32)]
    public string Category { get; set; } = "news";

    [MaxLength(500)]
    public string BannerImageUrl { get; set; }

    /// <summary>Optional "read more" target · e.g. the website article.</summary>
    [MaxLength(500)]
    public string LinkUrl { get; set; }

    [MaxLength(64)]
    public string AuthorUid { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AuthorDisplay { get; set; } = "Sphene Team";

    public bool IsPinned { get; set; }
    public bool IsDraft { get; set; }
    public DateTime PublishedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

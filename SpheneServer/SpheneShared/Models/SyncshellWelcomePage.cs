using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

public class SyncshellWelcomePage
{
    [Key]
    public string GroupGID { get; set; }
    public Group Group { get; set; }
    
    // Welcome page content
    [MaxLength(2000)]
    public string? WelcomeText { get; set; }
    
    // Image data stored as base64 string
    public string? WelcomeImageBase64 { get; set; }
    
    // Image metadata
    [MaxLength(100)]
    public string? ImageFileName { get; set; }
    [MaxLength(50)]
    public string? ImageContentType { get; set; }
    public long? ImageSize { get; set; }
    
    // Settings
    public bool IsEnabled { get; set; } = true;
    public bool ShowOnJoin { get; set; } = true;
    public bool ShowOnAreaBoundJoin { get; set; } = true;

    // Profile fields
    [MaxLength(160)]
    public string? Tagline { get; set; }

    [MaxLength(9)]
    public string? AccentColor { get; set; }

    public string? AvatarImageBase64 { get; set; }

    [MaxLength(100)]
    public string? AvatarFileName { get; set; }
    [MaxLength(50)]
    public string? AvatarContentType { get; set; }
    public long? AvatarSize { get; set; }

    public string? SectionsJson { get; set; }

    // Community content blocks
    [MaxLength(4000)]
    public string? RulesText { get; set; }

    public string? LinksJson { get; set; }

    public string? VisibleSectionsJson { get; set; }

    // Owner-configured overview widget order (JSON list of widget keys)
    public string? OverviewLayoutJson { get; set; }

    public bool IsListedPublicly { get; set; } = false;

    /// <summary>Serialized <c>SyncshellPublishingPolicyDto</c> - per-shell
    /// publish modes + direct-publish bit mask. Null → safe defaults.</summary>
    public string? PublishingPolicyJson { get; set; }

    /// <summary>Serialized <c>EventDestinationDto</c> - the shell's permanent
    /// community location snapshot. Kept when unpublished so staff can
    /// re-publish the same address; only shown to non-staff while
    /// <see cref="DestinationPublished"/> is set.</summary>
    public string? DestinationJson { get; set; }

    /// <summary>Explicit publication flag - an address never becomes public
    /// on its own; requires a deliberate publish action.</summary>
    public bool DestinationPublished { get; set; } = false;

    // Media references - hash-addressed file-server media replacing base64
    // (dual-read: hash preferred while it exists, base64 column is the fallback).
    // The DTO's "Banner" slot maps onto WelcomeImageBase64 - one media slot.
    [MaxLength(40)]
    public string? WelcomeMediaHash { get; set; }
    [MaxLength(40)]
    public string? AvatarMediaHash { get; set; }

    // Timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
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
    
    // Timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
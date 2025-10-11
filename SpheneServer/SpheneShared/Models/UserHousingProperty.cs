using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

public class UserHousingProperty
{
    [Key]
    public long Id { get; set; }
    
    [MaxLength(10)]
    public string UserUID { get; set; }
    public User User { get; set; }
    
    // Location information
    public uint ServerId { get; set; }
    public uint MapId { get; set; }
    public uint TerritoryId { get; set; }
    public uint DivisionId { get; set; }
    public uint WardId { get; set; }
    public uint HouseId { get; set; }
    public uint RoomId { get; set; }
    public bool IsIndoor { get; set; }
    
    // Preferences for syncshells
    public bool AllowOutdoor { get; set; } = true;
    public bool AllowIndoor { get; set; } = true;
    
    // Syncshell type preferences (what type of syncshells the user prefers to create/join)
    public bool PreferOutdoorSyncshells { get; set; } = true;
    public bool PreferIndoorSyncshells { get; set; } = true;
    
    // Metadata
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
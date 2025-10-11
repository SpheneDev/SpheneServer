using System.ComponentModel.DataAnnotations;
using Sphene.API.Dto.Group;

namespace SpheneShared.Models;

public class AreaBoundSyncshell
{
    [Key]
    public string GroupGID { get; set; }
    public Group Group { get; set; }
    
    // Multiple locations for this syncshell
    public List<AreaBoundLocation> Locations { get; set; } = new();
    
    // Global settings for all locations
    public bool AutoBroadcastEnabled { get; set; }
    public int MaxAutoJoinUsers { get; set; }
    public bool RequireOwnerPresence { get; set; }
    public bool NotifyOnUserEnter { get; set; }
    public bool NotifyOnUserLeave { get; set; }
    public string? CustomJoinMessage { get; set; }
    
    // Rules and consent system
    public string? JoinRules { get; set; }
    public int RulesVersion { get; set; } = 1;
    public bool RequireRulesAcceptance { get; set; } = false;
    
    public DateTime CreatedAt { get; set; }
}
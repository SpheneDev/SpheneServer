using System.ComponentModel.DataAnnotations;
using Sphene.API.Dto.Group;

namespace SpheneShared.Models;

public class AreaBoundLocation
{
    [Key]
    public int Id { get; set; }
    
    public string GroupGID { get; set; }
    public AreaBoundSyncshell AreaBoundSyncshell { get; set; }
    
    // Location information
    public ushort ServerId { get; set; }
    public uint MapId { get; set; }
    public uint TerritoryId { get; set; }
    public uint DivisionId { get; set; }
    public ushort WardId { get; set; }
    public ushort HouseId { get; set; }
    public byte RoomId { get; set; }
    
    // Location-specific settings
    public AreaMatchingMode MatchingMode { get; set; } = AreaMatchingMode.ExactMatch;
    public string? LocationName { get; set; } // Optional name for the location (e.g., "Main House", "Garden")
    
    public DateTime CreatedAt { get; set; }
}
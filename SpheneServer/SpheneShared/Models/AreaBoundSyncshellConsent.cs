using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

public class AreaBoundSyncshellConsent
{
    [Key]
    public long Id { get; set; }
    
    public string UserUID { get; set; }
    public User User { get; set; }
    
    public string SyncshellGID { get; set; }
    public Group Syncshell { get; set; }
    
    public bool HasAccepted { get; set; }
    public DateTime ConsentGivenAt { get; set; }
    public DateTime? LastRulesAcceptedAt { get; set; }
    
    // Track which version of rules was accepted
    public int AcceptedRulesVersion { get; set; }
}
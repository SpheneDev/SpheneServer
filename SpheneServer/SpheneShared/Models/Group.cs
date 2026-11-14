using System.ComponentModel.DataAnnotations;
using Sphene.API.Data.Enum;

namespace SpheneShared.Models;

public class Group
{
    [Key]
    [MaxLength(20)]
    public string GID { get; set; }
    public string OwnerUID { get; set; }
    public User Owner { get; set; }
    [MaxLength(50)]
    public string Alias { get; set; }
    /// <summary>
    /// How new members may join - <see cref="SyncshellJoinPolicy.PasswordProtected"/>
    /// keeps the original password/temp-invite behavior for existing rows.
    /// <see cref="InvitesEnabled"/>=false always overrides to effectively Closed.
    /// </summary>
    public SyncshellJoinPolicy JoinPolicy { get; set; } = SyncshellJoinPolicy.PasswordProtected;
    public bool InvitesEnabled { get; set; }
    public string HashedPassword { get; set; }
    public bool PreferDisableSounds { get; set; }
    public bool PreferDisableAnimations { get; set; }
    public bool PreferDisableVFX { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

public class User
{
    [Key]
    [MaxLength(10)]
    public string UID { get; set; }
    [Timestamp]
    public byte[] Timestamp { get; set; }

    public bool IsModerator { get; set; } = false;

    public bool IsAdmin { get; set; } = false;

    public bool IsSupporter { get; set; } = false;

    /// <summary>Server-stamped start of the one-time 24h supporter trial -
    /// non-null means the trial has been used (whether active or expired).</summary>
    public DateTime? SupporterTrialStartUtc { get; set; }

    public DateTime LastLoggedIn { get; set; }
    [MaxLength(15)]
    public string Alias { get; set; }

    [MaxLength(64)]
    public string? DisplayName { get; set; }

    public DateTime? AliasChangedAt { get; set; }

    /// <summary>Bitmask of <see cref="API.Data.Enum.UserNotificationKind"/> values the user
    /// muted server-side. 0 = everything delivered (default until settings UI lands).</summary>
    public long NotificationDisabledKinds { get; set; }
}

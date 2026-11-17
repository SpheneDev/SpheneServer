using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

/// <summary>
/// Client-encrypted backup of the account's E2EE device keyring plus TOFU
/// baseline. Opaque ciphertext for the server - the wrap key is derived
/// client-side from the secret key, of which only a hash ever reaches the
/// server. One row per user; last write wins.
/// </summary>
public class ChatKeyBackup
{
    [MaxLength(10)]
    public string UserUID { get; set; }
    public User User { get; set; }

    /// <summary>AES-256-GCM wrapped keyring: format byte || nonce ||
    /// ciphertext || tag.</summary>
    public byte[] Blob { get; set; }

    /// <summary>Monotonic version counter - every write increments it and a
    /// compare-and-swap update only lands while the caller's expected
    /// revision still matches.</summary>
    public long Revision { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

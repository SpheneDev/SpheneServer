using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

/// <summary>
/// Public E2EE identity of one client installation (device). The account owns
/// the key set; private keys never leave the devices. Revoked keys stay for
/// audit/history verification but cannot be addressed by new messages.
/// </summary>
public class ChatDeviceKey
{
    [MaxLength(10)]
    public string UserUID { get; set; }
    public User User { get; set; }

    [MaxLength(64)]
    public string DeviceId { get; set; }

    /// <summary>65-byte uncompressed P-256 point (DHKEM key).</summary>
    public byte[] PublicKey { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}

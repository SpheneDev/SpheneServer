using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

/// <summary>
/// One device-addressed HPKE copy of a sealed chat message. Opaque to the
/// server - the row only carries routing/provenance metadata needed for
/// authorization and per-caller filtering.
/// </summary>
public class ChatMessageCipher
{
    public Guid MessageId { get; set; }
    public ChatMessage Message { get; set; }

    /// <summary>Participant (sender or recipient) this copy decrypts for.</summary>
    [MaxLength(10)]
    public string ForUID { get; set; }
    [MaxLength(64)]
    public string ForDeviceId { get; set; }

    /// <summary>Installation that produced the seal (author or migrator) and
    /// the public key the recipient authenticates against. Pinned at write
    /// time so history stays verifiable after later key rotation.</summary>
    [MaxLength(10)]
    public string SealerUID { get; set; }
    [MaxLength(64)]
    public string SealerDeviceId { get; set; }
    public byte[] SealerPublicKey { get; set; }

    /// <summary>HPKE v1 envelope: version byte || DHKEM enc || AEAD ct||tag.</summary>
    public byte[] Blob { get; set; }
}

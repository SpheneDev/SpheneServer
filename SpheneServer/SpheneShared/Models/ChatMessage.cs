using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

public class ChatMessage
{
    [Key]
    public Guid Id { get; set; }
    public User Sender { get; set; }
    public string SenderUID { get; set; }
    public User Recipient { get; set; }
    public string RecipientUID { get; set; }

    /// <summary>Plaintext body. Kept for pre-E2EE history rows; null once a
    /// row is natively sealed or a legacy row was migrated to ciphertext.</summary>
    [MaxLength(500)]
    public string? Message { get; set; }
    public DateTime SentAt { get; set; }
    public DateTime? ReadAt { get; set; }

    /// <summary>0 = plaintext, 1 = sealed at send time, 2 = legacy plaintext
    /// re-sealed by a participant after the fact (Kind mirrors
    /// API.Data.Enum.ChatMessageKind).</summary>
    public byte Kind { get; set; }

    /// <summary>Migration audit fields - the migrator is a chat participant,
    /// never the server (the server holds no private keys).</summary>
    public DateTime? MigratedAtUtc { get; set; }
    [MaxLength(10)]
    public string? MigratedByUID { get; set; }

    /// <summary>Per-device cipher copies; only for Kind 1/2 rows.</summary>
    public List<ChatMessageCipher> Ciphers { get; set; } = [];
}

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
    [MaxLength(500)]
    public string Message { get; set; }
    public DateTime SentAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

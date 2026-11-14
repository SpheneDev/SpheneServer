using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SpheneShared.Models;

public class UserBlock
{
    public int Id { get; set; }
    public User User { get; set; }
    public string UserUID { get; set; }
    public User BlockedUser { get; set; }
    public string BlockedUserUID { get; set; }
    public DateTime BlockedOn { get; set; }
}

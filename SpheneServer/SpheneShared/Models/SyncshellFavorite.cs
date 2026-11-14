using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

/// <summary>
/// A user's bookmarked syncshell - favorites keep a listed/area-bound shell
/// reachable from the "Followed" surface so its community page (events,
/// news, photos) stays visible without membership. Purely a viewer-side
/// record; the shell never learns who follows it.
/// </summary>
public class SyncshellFavorite
{
    [Key]
    public long Id { get; set; }

    [MaxLength(40)]
    public string GroupGID { get; set; } = string.Empty;
    public Group Group { get; set; } = null!;

    [MaxLength(40)]
    public string UserUID { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
}

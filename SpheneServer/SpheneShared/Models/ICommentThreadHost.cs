namespace SpheneShared.Models;

/// <summary>Entities that host a comment thread - the transactional comment
/// service locks THIS row and maintains the denormalized counter on it.</summary>
public interface ICommentThreadHost
{
    /// <summary>Denormalized visible+deleted comment count.</summary>
    int CommentCount { get; set; }
    /// <summary>Thread lock - non-privileged comment writes rejected.</summary>
    bool CommentsLocked { get; set; }
}

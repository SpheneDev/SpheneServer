using Microsoft.EntityFrameworkCore;
using Sphene.API.Data.Enum;
using SpheneShared.Data;
using SpheneShared.Models;

namespace SpheneServer.Services;

/// <summary>
/// Transactional comment write path - the single place where
/// <see cref="CommunityComment"/> rows are created, edited, tombstoned or
/// moderated, and where the denormalized <c>comment_count</c> /
/// <c>reply_count</c> counters move.
///
/// Concurrency: on relational providers the PARENT POST row (news/event/blog)
/// is locked <c>FOR UPDATE</c> inside a transaction, so parallel comment
/// writes serialize - counters can never drift and a lock-flip cannot pass
/// an in-flight create. Non-relational test providers skip the raw lock; the
/// state machine is identical.
///
/// Threading model (depth-2): roots carry parent_id IS NULL AND root_id IS
/// NULL - the generated id never needs pre-fetching. Replies carry both;
/// replying TO a reply flattens onto its root. The CHECK constraint pins the
/// invariant so a committed reply can never have a null root_id.
///
/// Moderation scope: <see cref="ModerationScope"/> records WHICH hat acted -
/// blog-owner hides stay owner-restorable, platform-mod hides do not. The
/// state enum alone cannot express that, so the comment row stamps
/// <see cref="CommunityComment.ModeratedByScope"/> on every transition.
/// </summary>
public static class CommunityCommentService
{
    /// <summary>Actor hats - stored on the row and in the audit log.</summary>
    public enum ModerationScope
    {
        None = 0,
        BlogOwner = 1,
        CommunityStaff = 2,
        Platform = 3,
    }

    public enum ModAction
    {
        Hide = 0,
        Restore = 1,
        Remove = 2,
        LockThread = 3,
        UnlockThread = 4,
    }

    public sealed record CommentOutcome(
        bool Success,
        string? Error = null,
        CommunityComment? Comment = null,
        bool Changed = false,
        /// <summary>Recipient hints for the hub's notification fan-out.</summary>
        string? ParentAuthorUid = null,
        string? TargetAuthorUid = null);

    /// <summary>Body cap - enforced here AND at the hub boundary.</summary>
    public const int MaxBodyLength = 4000;

    // ------------------------------------------------------------------
    //  Create (root or reply)
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a root comment or reply. <paramref name="replyToId"/> &gt; 0
    /// resolves to the thread root - replying to a reply flattens onto its
    /// root (depth-2). The root must exist, be a root row and belong to the
    /// same target - anything else is rejected (requirement 1).
    /// <paramref name="lockedOverride"/> lets staff/owners write past a
    /// thread lock; the caller resolves that via ACL.
    /// </summary>
    public static async Task<CommentOutcome> CreateAsync(
        SpheneDbContext db, CommentTargetKind targetKind, long targetId,
        string? groupGid, string? ownerUid, string authorUid, string body,
        long? replyToId, bool lockedOverride, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > MaxBodyLength)
            return new(false, "invalid-body");
        var plain = Utils.MarkdownText.StripPreservingLines(body);
        if (plain.Length == 0) return new(false, "invalid-body");

        return await InParentTxnAsync(db, targetKind, targetId, async parent =>
        {
            if (parent == null) return new(false, "not-found");
            if (IsLocked(parent) && !lockedOverride) return new(false, "locked");

            long? rootId = null;
            string? parentAuthor = null;
            if (replyToId is > 0)
            {
                var anchor = await db.CommunityComments
                    .FirstOrDefaultAsync(c => c.Id == replyToId.Value).ConfigureAwait(false);
                // The reply target must be a comment on the SAME host target -
                // cross-target anchoring would corrupt root chains.
                if (anchor == null || anchor.TargetKind != (int)targetKind || anchor.TargetId != targetId)
                    return new(false, "parent-not-found");
                await RefreshAsync(db, anchor).ConfigureAwait(false);
                if (anchor.State != (int)CommentState.Visible)
                    return new(false, "parent-unavailable");
                var root = anchor.ParentId == null ? anchor : await db.CommunityComments
                    .FirstOrDefaultAsync(c => c.Id == anchor.RootId).ConfigureAwait(false);
                if (root == null || root.ParentId != null
                    || root.TargetKind != (int)targetKind || root.TargetId != targetId)
                    return new(false, "root-mismatch");
                rootId = root.Id;
                root.ReplyCount++;
                parentAuthor = anchor.AuthorUid;
            }

            var comment = new CommunityComment
            {
                TargetKind = (int)targetKind,
                TargetId = targetId,
                GroupGID = groupGid,
                OwnerUid = ownerUid,
                AuthorUid = authorUid,
                ParentId = rootId,
                RootId = rootId,
                Body = plain,
                State = (int)CommentState.Visible,
                CreatedAt = utcNow,
            };
            db.CommunityComments.Add(comment);
            parent.CommentCount++;
            Audit(db, authorUid, (int)ModerationScope.None, "create", (int)targetKind, targetId,
                null, groupGid, ownerUid, null, utcNow);
            await db.SaveChangesAsync().ConfigureAwait(false);
            return new(true, Comment: comment, Changed: true,
                ParentAuthorUid: parentAuthor, TargetAuthorUid: TargetAuthorOf(parent));
        }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    //  Edit / author delete
    // ------------------------------------------------------------------

    /// <summary>Author edit - own visible comments only. Stamps EditedAt.</summary>
    public static async Task<CommentOutcome> EditAsync(
        SpheneDbContext db, long commentId, string uid, string body, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > MaxBodyLength)
            return new(false, "invalid-body");
        var plain = Utils.MarkdownText.StripPreservingLines(body);
        if (plain.Length == 0) return new(false, "invalid-body");
        var comment = await db.CommunityComments.FirstOrDefaultAsync(c => c.Id == commentId)
            .ConfigureAwait(false);
        if (comment == null) return new(false, "not-found");
        await RefreshAsync(db, comment).ConfigureAwait(false);
        if (comment.AuthorUid != uid) return new(false, "forbidden");
        if (comment.State != (int)CommentState.Visible) return new(false, "state");
        comment.Body = plain;
        comment.EditedAt = utcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new(true, Comment: comment, Changed: true);
    }

    /// <summary>Author soft-delete - tombstones the row, decrements the post
    /// counter under the parent lock.</summary>
    public static async Task<CommentOutcome> DeleteByAuthorAsync(
        SpheneDbContext db, long commentId, string uid, DateTime utcNow)
    {
        var comment = await db.CommunityComments.FirstOrDefaultAsync(c => c.Id == commentId)
            .ConfigureAwait(false);
        if (comment == null) return new(false, "not-found");
        await RefreshAsync(db, comment).ConfigureAwait(false);
        if (comment.AuthorUid != uid) return new(false, "forbidden");
        if (comment.State != (int)CommentState.Visible) return new(true, Comment: comment);

        return await InParentTxnAsync(db, (CommentTargetKind)comment.TargetKind, comment.TargetId,
            async parent =>
            {
                if (comment.State != (int)CommentState.Visible)
                    return new(true, Comment: comment);
                Stamp(comment, CommentState.AuthorDeleted, uid, (int)ModerationScope.None, null, utcNow);
                if (parent != null) parent.CommentCount = Math.Max(0, parent.CommentCount - 1);
                Audit(db, uid, (int)ModerationScope.None, "delete", comment.TargetKind,
                    comment.TargetId, comment.Id, comment.GroupGID, comment.OwnerUid, null, utcNow);
                await db.SaveChangesAsync().ConfigureAwait(false);
                return new(true, Comment: comment, Changed: true);
            }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    //  Moderation - hide / restore / remove / thread lock
    // ------------------------------------------------------------------

    /// <summary>
    /// Applies a moderation transition. Authority resolution lives in the hub;
    /// THIS method enforces the state machine + the scope rules that the
    /// state enum alone cannot:
    ///   • Restore of ModeratorHidden requires scope-appropriate authority -
    ///     a BlogOwner may only restore rows they themselves hid
    ///     (ModeratedByScope == BlogOwner); platform hides are never
    ///     owner-restorable. CommunityStaff/Platform restore any hidden row.
    ///   • ModeratorRemoved is terminal - nobody restores it.
    ///   • Lock/Unlock flips the parent post's flag.
    /// </summary>
    public static async Task<CommentOutcome> ModerateAsync(
        SpheneDbContext db, CommentTargetKind targetKind, long targetId,
        long? commentId, ModAction action, string actorUid,
        ModerationScope actorScope, string? reason, DateTime utcNow)
    {
        return await InParentTxnAsync(db, targetKind, targetId, async parent =>
        {
            if (parent == null) return new(false, "not-found");

            if (action is ModAction.LockThread or ModAction.UnlockThread)
            {
                var locked = action == ModAction.LockThread;
                if (IsLocked(parent) == locked) return new(true, Changed: false);
                SetLocked(parent, locked);
                Audit(db, actorUid, (int)actorScope, locked ? "lock" : "unlock",
                    (int)targetKind, targetId, null, parent is SyncshellCommunityNewsPost or SyncshellCommunityEvent
                        ? GroupGidOf(parent) : null,
                    OwnerUidOf(parent), reason, utcNow);
                await db.SaveChangesAsync().ConfigureAwait(false);
                return new(true, Changed: true);
            }

            if (commentId is not > 0) return new(false, "comment-required");
            var comment = await db.CommunityComments
                .FirstOrDefaultAsync(c => c.Id == commentId.Value).ConfigureAwait(false);
            if (comment == null || comment.TargetKind != (int)targetKind || comment.TargetId != targetId)
                return new(false, "not-found");
            await RefreshAsync(db, comment).ConfigureAwait(false);

            switch (action)
            {
                case ModAction.Hide:
                    if (comment.State == (int)CommentState.ModeratorHidden)
                    {
                        // Already hidden - a higher-privilege actor takes over
                        // the stamp so restoration authority follows the
                        // strongest moderation actor, not the first one. This
                        // is the D12 rule: a platform re-hide must never leave
                        // a BlogOwner stamp an owner could later restore.
                        if (actorScope > (ModerationScope)comment.ModeratedByScope)
                            Stamp(comment, CommentState.ModeratorHidden, actorUid,
                                (int)actorScope, reason, utcNow);
                        else
                            return new(true, Comment: comment);
                        break;
                    }
                    if (comment.State != (int)CommentState.Visible) return new(true, Comment: comment);
                    Stamp(comment, CommentState.ModeratorHidden, actorUid, (int)actorScope, reason, utcNow);
                    parent.CommentCount = Math.Max(0, parent.CommentCount - 1);
                    break;

                case ModAction.Restore:
                    if (comment.State == (int)CommentState.ModeratorRemoved)
                        return new(false, "terminal");
                    if (comment.State == (int)CommentState.AuthorDeleted)
                        return new(false, "author-deleted");
                    if (comment.State != (int)CommentState.ModeratorHidden)
                        return new(true, Comment: comment);
                    // The D12 rule - owner may only restore their OWN hides.
                    if (actorScope == ModerationScope.BlogOwner
                        && comment.ModeratedByScope != (int)ModerationScope.BlogOwner)
                        return new(false, "scope");
                    Stamp(comment, CommentState.Visible, actorUid, (int)ModerationScope.None, null, utcNow);
                    parent.CommentCount++;
                    break;

                case ModAction.Remove:
                    if (comment.State == (int)CommentState.ModeratorRemoved)
                        return new(true, Comment: comment);
                    if (comment.State == (int)CommentState.Visible)
                        parent.CommentCount = Math.Max(0, parent.CommentCount - 1);
                    Stamp(comment, CommentState.ModeratorRemoved, actorUid, (int)actorScope, reason, utcNow);
                    break;

                default:
                    return new(false, "action");
            }

            Audit(db, actorUid, (int)actorScope, action.ToString().ToLowerInvariant(),
                (int)targetKind, targetId, comment.Id, comment.GroupGID, comment.OwnerUid, reason, utcNow);
            await db.SaveChangesAsync().ConfigureAwait(false);
            return new(true, Comment: comment, Changed: true);
        }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    /// <summary>Runs <paramref name="body"/> under the parent-post row lock
    /// (relational) or plainly (test providers). The lock serializes counter
    /// writes exactly like the RSVP path serializes seat counters.</summary>
    private static async Task<CommentOutcome> InParentTxnAsync(
        SpheneDbContext db, CommentTargetKind kind, long targetId,
        Func<ICommentThreadHost?, Task<CommentOutcome>> body)
    {
        if (db.Database.IsRelational())
        {
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
            await LockParentRowAsync(db, kind, targetId).ConfigureAwait(false);
            var parent = await LoadParentAsync(db, kind, targetId).ConfigureAwait(false);
            var outcome = await body(parent).ConfigureAwait(false);
            if (outcome.Success) await tx.CommitAsync().ConfigureAwait(false);
            else await tx.RollbackAsync().ConfigureAwait(false);
            return outcome;
        }
        var plain = await LoadParentAsync(db, kind, targetId).ConfigureAwait(false);
        return await body(plain).ConfigureAwait(false);
    }

    /// <summary>Locks the host row (news/event/blog) FOR UPDATE - the same
    /// serializer pattern as <c>ApplyRsvpAsync</c>.</summary>
    private static async Task LockParentRowAsync(SpheneDbContext db, CommentTargetKind kind, long targetId)
    {
        var table = kind switch
        {
            CommentTargetKind.News => "syncshell_community_news",
            CommentTargetKind.Event => "syncshell_community_events",
            CommentTargetKind.Blog => "user_profile_blog_posts",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        // The table name comes from the closed enum set - never user input.
        await db.Database.ExecuteSqlRawAsync(
            $"SELECT 1 FROM {table} WHERE id = {{0}} FOR UPDATE", targetId).ConfigureAwait(false);
    }

    private static async Task<ICommentThreadHost?> LoadParentAsync(
        SpheneDbContext db, CommentTargetKind kind, long targetId)
    {
        var parent = kind switch
        {
            CommentTargetKind.News => (ICommentThreadHost?)await db.SyncshellCommunityNews
                .FirstOrDefaultAsync(n => n.Id == targetId).ConfigureAwait(false),
            CommentTargetKind.Event => await db.SyncshellCommunityEvents
                .FirstOrDefaultAsync(e => e.Id == targetId).ConfigureAwait(false),
            CommentTargetKind.Blog => await db.UserProfileBlogPosts
                .FirstOrDefaultAsync(b => b.Id == targetId).ConfigureAwait(false),
            _ => null,
        };
        if (parent != null) await RefreshAsync(db, parent).ConfigureAwait(false);
        return parent;
    }

    /// <summary>Re-reads a tracked entity so state-machine decisions never
    /// run on values a concurrent actor already committed - a long-lived
    /// hub context can otherwise mask another actor's moderation stamp
    /// (the D12 rule) or lock flag. Under the parent row lock this yields
    /// exactly the committed row; on InMemory it re-reads the store.</summary>
    private static async Task RefreshAsync(SpheneDbContext db, object entity)
    {
        var entry = db.Entry(entity);
        if (entry.State is EntityState.Unchanged or EntityState.Modified)
            await entry.ReloadAsync().ConfigureAwait(false);
    }

    private static bool IsLocked(ICommentThreadHost parent) => parent.CommentsLocked;
    private static void SetLocked(ICommentThreadHost parent, bool locked) => parent.CommentsLocked = locked;
    private static string? GroupGidOf(ICommentThreadHost parent) => parent switch
    {
        SyncshellCommunityNewsPost n => n.GroupGID,
        SyncshellCommunityEvent e => e.GroupGID,
        _ => null,
    };
    private static string? OwnerUidOf(ICommentThreadHost parent) => parent is UserProfileBlogPost b ? b.UserUID : null;
    private static string? TargetAuthorOf(ICommentThreadHost parent) => parent switch
    {
        SyncshellCommunityNewsPost n => n.AuthorUid,
        SyncshellCommunityEvent e => e.AuthorUid,
        UserProfileBlogPost b => b.UserUID,
        _ => null,
    };

    private static void Stamp(CommunityComment c, CommentState state, string actorUid,
        int scope, string? reason, DateTime utcNow)
    {
        c.State = (int)state;
        c.ModeratedByUid = actorUid;
        c.ModeratedByScope = scope;
        c.ModerationReason = reason;
        c.ModeratedAt = utcNow;
    }

    /// <summary>Appends an immutable audit row - every moderation transition
    /// records actor + hat + reason so restore authority never depends on
    /// the comment's current state alone.</summary>
    public static void Audit(SpheneDbContext db, string actorUid, int actorScope,
        string action, int targetKind, long targetId, long? commentId,
        string? groupGid, string? ownerUid, string? reason, DateTime utcNow,
        long? reportId = null)
    {
        db.CommunityModerationLogs.Add(new CommunityModerationLog
        {
            ActorUid = actorUid,
            ActorScope = actorScope,
            Action = action,
            TargetKind = targetKind,
            TargetId = targetId,
            CommentId = commentId,
            ReportId = reportId,
            GroupGID = groupGid,
            OwnerUid = ownerUid,
            Reason = reason,
            CreatedAt = utcNow,
        });
    }
}

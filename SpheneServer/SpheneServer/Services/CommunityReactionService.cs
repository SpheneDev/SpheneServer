using Microsoft.EntityFrameworkCore;
using SpheneShared.Data;
using SpheneShared.Models;

namespace SpheneServer.Services;

/// <summary>
/// State-based reaction writes - the contract is
/// <c>SetReaction(kind, Set=true/false)</c>, never a toggle. The composite PK
/// (target_kind, target_id, user_uid, kind) is the uniqueness authority:
///
///   • Set   → INSERT … ON CONFLICT DO NOTHING - concurrent or repeated
///             calls can never duplicate or erase an existing reaction.
///   • Remove → DELETE - harmless when the row is already absent.
///
/// <see cref="ReactionOutcome.Inserted"/> tells the hub whether THIS call
/// performed the insert - reaction notifications fire only on a real
/// 0→1 transition, never on redundant re-sets.
/// </summary>
public static class CommunityReactionService
{
    public sealed record ReactionOutcome(
        bool Success,
        string? Error = null,
        /// <summary>This call inserted a previously-absent row.</summary>
        bool Inserted = false,
        /// <summary>This call deleted an existing row.</summary>
        bool Removed = false);

    /// <summary>Ensures the reaction row exists (Set=true) or is absent
    /// (Set=false). Idempotent under concurrent and repeated requests.</summary>
    public static async Task<ReactionOutcome> ApplyAsync(
        SpheneDbContext db, int targetKind, long targetId, string uid,
        int kind, bool set, string? groupGid, string? ownerUid, DateTime utcNow)
    {
        if (db.Database.IsRelational())
        {
            if (set)
            {
                var inserted = await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO community_reactions
                        (target_kind, target_id, user_uid, kind, group_gid, owner_uid, created_at)
                    VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6})
                    ON CONFLICT (target_kind, target_id, user_uid, kind) DO NOTHING
                    """, targetKind, targetId, uid, kind, groupGid, ownerUid, utcNow)
                    .ConfigureAwait(false);
                return new(true, Inserted: inserted > 0);
            }
            var removed = await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM community_reactions
                WHERE target_kind = {0} AND target_id = {1} AND user_uid = {2} AND kind = {3}
                """, targetKind, targetId, uid, kind).ConfigureAwait(false);
            return new(true, Removed: removed > 0);
        }

        var existing = await db.CommunityReactions.FindAsync(targetKind, targetId, uid, kind)
            .ConfigureAwait(false);
        if (set)
        {
            if (existing != null) return new(true);
            db.CommunityReactions.Add(new CommunityReaction
            {
                TargetKind = targetKind,
                TargetId = targetId,
                UserUID = uid,
                Kind = kind,
                GroupGID = groupGid,
                OwnerUid = ownerUid,
                CreatedAt = utcNow,
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
            return new(true, Inserted: true);
        }
        if (existing == null) return new(true);
        db.CommunityReactions.Remove(existing);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new(true, Removed: true);
    }

    /// <summary>Aggregate counts per kind + the viewer's own kinds for one
    /// target - GROUP BY read, no denormalized counters to drift.</summary>
    public static async Task<(Dictionary<int, int> Counts, List<int> ViewerKinds, int Total)>
        SummaryAsync(SpheneDbContext db, int targetKind, long targetId, string viewerUid)
    {
        var grouped = await db.CommunityReactions
            .Where(r => r.TargetKind == targetKind && r.TargetId == targetId)
            .GroupBy(r => r.Kind)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync().ConfigureAwait(false);
        var viewer = await db.CommunityReactions
            .Where(r => r.TargetKind == targetKind && r.TargetId == targetId && r.UserUID == viewerUid)
            .Select(r => r.Kind).ToListAsync().ConfigureAwait(false);
        var counts = grouped.ToDictionary(g => g.Key, g => g.Count);
        return (counts, viewer, grouped.Sum(g => g.Count));
    }
}

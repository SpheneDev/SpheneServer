using Microsoft.EntityFrameworkCore;
using SpheneShared.Data;
using SpheneShared.Models;

namespace SpheneServer.Services;

/// <summary>
/// State-based reaction writes - the contract is
/// <c>SetReaction(emoji, Set=true/false)</c>, never a toggle. The composite PK
/// (target_kind, target_id, user_uid, emoji_seq) is the uniqueness authority:
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
        string emoji, bool set, string? groupGid, string? ownerUid, DateTime utcNow)
    {
        // The frozen int column is write-through only: reverse-mapped when
        // the emoji is one of the legacy canonical glyphs, -1 otherwise.
        var legacyKind = Sphene.API.Data.Enum.ReactionEmojiMap.KindFor(emoji) is { } k ? (int)k : -1;
        if (db.Database.IsRelational())
        {
            if (set)
            {
                var inserted = await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO community_reactions
                        (target_kind, target_id, user_uid, emoji_seq, kind, group_gid, owner_uid, created_at)
                    VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7})
                    ON CONFLICT (target_kind, target_id, user_uid, emoji_seq) DO NOTHING
                    """, targetKind, targetId, uid, emoji, legacyKind, groupGid, ownerUid, utcNow)
                    .ConfigureAwait(false);
                return new(true, Inserted: inserted > 0);
            }
            var removed = await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM community_reactions
                WHERE target_kind = {0} AND target_id = {1} AND user_uid = {2} AND emoji_seq = {3}
                """, targetKind, targetId, uid, emoji).ConfigureAwait(false);
            return new(true, Removed: removed > 0);
        }

        var existing = await db.CommunityReactions.FindAsync(targetKind, targetId, uid, emoji)
            .ConfigureAwait(false);
        if (set)
        {
            if (existing != null) return new(true);
            db.CommunityReactions.Add(new CommunityReaction
            {
                TargetKind = targetKind,
                TargetId = targetId,
                UserUID = uid,
                EmojiSeq = emoji,
                Kind = legacyKind,
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

    /// <summary>Aggregate counts per emoji + the viewer's own emojis for one
    /// target - GROUP BY read, no denormalized counters to drift. The
    /// int-keyed legacy maps aggregate reverse-mappable glyphs so pre-emoji
    /// clients keep reading coherent counts.</summary>
    public static async Task<(Dictionary<string, int> EmojiCounts, List<string> ViewerEmojis,
        Dictionary<int, int> Counts, List<int> ViewerKinds, int Total)>
        SummaryAsync(SpheneDbContext db, int targetKind, long targetId, string viewerUid)
    {
        var grouped = await db.CommunityReactions
            .Where(r => r.TargetKind == targetKind && r.TargetId == targetId)
            .GroupBy(r => r.EmojiSeq)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync().ConfigureAwait(false);
        var viewerEmojis = await db.CommunityReactions
            .Where(r => r.TargetKind == targetKind && r.TargetId == targetId && r.UserUID == viewerUid)
            .Select(r => r.EmojiSeq).ToListAsync().ConfigureAwait(false);
        var emojiCounts = new Dictionary<string, int>(grouped.Count);
        var counts = new Dictionary<int, int>();
        var total = 0;
        foreach (var g in grouped)
        {
            if (g.Key.Length > 0) emojiCounts[g.Key] = g.Count;
            if (Sphene.API.Data.Enum.ReactionEmojiMap.KindFor(g.Key) is { } legacy)
            {
                counts[(int)legacy] = counts.GetValueOrDefault((int)legacy) + g.Count;
            }
            total += g.Count;
        }
        var viewerKinds = new List<int>();
        foreach (var e in viewerEmojis)
        {
            if (Sphene.API.Data.Enum.ReactionEmojiMap.KindFor(e) is { } legacy
                && !viewerKinds.Contains((int)legacy))
            {
                viewerKinds.Add((int)legacy);
            }
        }
        return (emojiCounts, viewerEmojis, counts, viewerKinds, total);
    }
}

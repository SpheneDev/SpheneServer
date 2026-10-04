using System.Collections.Concurrent;
using Sphene.API.Dto.Visibility;

namespace SpheneServer.Services;

/// <summary>
/// Result of processing a visibility report or disconnect event.
/// Contains the DTO to broadcast (if any) and the list of recipient UIDs that should receive it.
/// </summary>
public sealed record MutualVisibilityUpdateResult(MutualVisibilityDto? Dto, IReadOnlyList<string> RecipientUids);

/// <summary>
/// Manages mutual visibility state between paired users.
/// Extracted from <c>SpheneHub</c> so the visibility state machine can be unit-tested
/// without requiring SignalR, database, or Redis infrastructure.
/// </summary>
/// <remarks>
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/> and per-state field volatility.
/// The caller is responsible for actually broadcasting the returned DTOs via SignalR.
/// </remarks>
public sealed class MutualVisibilityTracker
{
    private readonly ConcurrentDictionary<string, MutualVisibilityState> _states = new(StringComparer.Ordinal);

    /// <summary>
    /// Processes a visibility report from one user about another and returns whether a DTO
    /// should be broadcast to one or both users.
    /// </summary>
    /// <param name="reporterUid">UID of the user reporting visibility.</param>
    /// <param name="targetUid">UID of the user being reported about.</param>
    /// <param name="isVisible">Whether the reporter sees the target.</param>
    /// <param name="now">Current UTC timestamp.</param>
    /// <returns>Update result containing the DTO to broadcast (if the mutual state changed) and the recipients.</returns>
    public MutualVisibilityUpdateResult ProcessReport(string reporterUid, string targetUid, bool isVisible, DateTime now)
    {
        var (uidA, uidB) = OrderUids(reporterUid, targetUid);
        var key = BuildKey(uidA, uidB);

        var state = _states.GetOrAdd(key, _ => new MutualVisibilityState { UidA = uidA, UidB = uidB });

        if (string.Equals(reporterUid, uidA, StringComparison.Ordinal))
        {
            state.LastSeenA = isVisible;
            state.LastReportA = now;
        }
        else
        {
            state.LastSeenB = isVisible;
            state.LastReportB = now;
        }

        bool newMutual = state.LastSeenA && state.LastSeenB;

        if (newMutual == state.IsMutual)
        {
            return new MutualVisibilityUpdateResult(null, Array.Empty<string>());
        }

        state.IsMutual = newMutual;
        var dto = new MutualVisibilityDto(new(uidA), new(uidB), newMutual, now);
        return new MutualVisibilityUpdateResult(dto, new[] { uidA, uidB });
    }

    /// <summary>
    /// Resets the visibility state for a disconnecting user, marking their side as invisible.
    /// Returns DTOs to broadcast for all pairs where the mutual state changed as a result.
    /// </summary>
    /// <param name="userUid">UID of the disconnecting user.</param>
    /// <param name="now">Current UTC timestamp.</param>
    /// <returns>One update result per affected pair; may be empty if no state changed.</returns>
    public IReadOnlyList<MutualVisibilityUpdateResult> ProcessDisconnect(string userUid, DateTime now)
    {
        var results = new List<MutualVisibilityUpdateResult>();

        foreach (var kvp in _states)
        {
            var state = kvp.Value;
            if (!string.Equals(state.UidA, userUid, StringComparison.Ordinal) &&
                !string.Equals(state.UidB, userUid, StringComparison.Ordinal))
                continue;

            if (string.Equals(state.UidA, userUid, StringComparison.Ordinal))
            {
                state.LastSeenA = false;
                state.LastReportA = now;
            }
            else
            {
                state.LastSeenB = false;
                state.LastReportB = now;
            }

            bool newMutual = state.LastSeenA && state.LastSeenB;
            if (newMutual == state.IsMutual)
                continue;

            state.IsMutual = newMutual;
            var dto = new MutualVisibilityDto(new(state.UidA), new(state.UidB), false, now);
            results.Add(new MutualVisibilityUpdateResult(dto, new[] { state.UidA, state.UidB }));
        }

        return results;
    }

    /// <summary>
    /// Gets the current mutual visibility DTOs for all pairs involving <paramref name="userUid"/>.
    /// Used on (re)connect to resend the current state to a reconnecting client.
    /// </summary>
    /// <param name="userUid">UID of the reconnecting user.</param>
    /// <param name="now">Current UTC timestamp.</param>
    /// <returns>A DTO for the reconnecting user, or null if no pairs exist.</returns>
    public MutualVisibilityDto? GetCurrentStateForUser(string userUid, DateTime now)
    {
        foreach (var kvp in _states)
        {
            var state = kvp.Value;
            if (!string.Equals(state.UidA, userUid, StringComparison.Ordinal) &&
                !string.Equals(state.UidB, userUid, StringComparison.Ordinal))
                continue;

            return new MutualVisibilityDto(new(state.UidA), new(state.UidB), state.IsMutual, now);
        }

        return null;
    }

    /// <summary>
    /// Gets all current mutual visibility DTOs for pairs involving <paramref name="userUid"/>.
    /// Unlike <see cref="GetCurrentStateForUser"/>, this returns one DTO per pair (a user may
    /// have multiple pairs).
    /// </summary>
    /// <param name="userUid">UID of the reconnecting user.</param>
    /// <param name="now">Current UTC timestamp.</param>
    /// <returns>All DTOs for the user's pairs; may be empty.</returns>
    public IReadOnlyList<MutualVisibilityDto> GetAllCurrentStatesForUser(string userUid, DateTime now)
    {
        var results = new List<MutualVisibilityDto>();
        foreach (var kvp in _states)
        {
            var state = kvp.Value;
            if (!string.Equals(state.UidA, userUid, StringComparison.Ordinal) &&
                !string.Equals(state.UidB, userUid, StringComparison.Ordinal))
                continue;

            results.Add(new MutualVisibilityDto(new(state.UidA), new(state.UidB), state.IsMutual, now));
        }
        return results;
    }

    /// <summary>
    /// Returns the current mutual visibility state for a pair, or null if no state exists.
    /// Primarily for diagnostics and testing.
    /// </summary>
    public (bool IsMutual, bool LastSeenA, bool LastSeenB)? GetState(string uidA, string uidB)
    {
        var (orderedA, orderedB) = OrderUids(uidA, uidB);
        var key = BuildKey(orderedA, orderedB);
        if (_states.TryGetValue(key, out var state))
        {
            return (state.IsMutual, state.LastSeenA, state.LastSeenB);
        }
        return null;
    }

    private static (string uidA, string uidB) OrderUids(string a, string b)
    {
        return string.Compare(a, b, StringComparison.Ordinal) <= 0 ? (a, b) : (b, a);
    }

    private static string BuildKey(string uidA, string uidB)
    {
        return string.Create(uidA.Length + uidB.Length + 1, (uidA, uidB), (span, state) =>
        {
            state.uidA.AsSpan().CopyTo(span);
            span[state.uidA.Length] = '|';
            state.uidB.AsSpan().CopyTo(span.Slice(state.uidA.Length + 1));
        });
    }

    /// <summary>
    /// Internal state for a single ordered pair. Public for testability.
    /// </summary>
    public sealed class MutualVisibilityState
    {
        public DateTime LastReportA { get; set; } = DateTime.MinValue;
        public DateTime LastReportB { get; set; } = DateTime.MinValue;
        public bool LastSeenA { get; set; } = false;
        public bool LastSeenB { get; set; } = false;
        public bool IsMutual { get; set; } = false;
        public string UidA { get; set; } = string.Empty;
        public string UidB { get; set; } = string.Empty;
    }
}

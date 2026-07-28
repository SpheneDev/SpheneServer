using System.Collections.Concurrent;

namespace SpheneServer.Services;

public class BatchAcknowledgmentSession
{
    public string SessionId { get; init; } = string.Empty;
    public string DataHash { get; init; } = string.Empty;
    public string SenderUid { get; init; } = string.Empty;
    public HashSet<string> PendingRecipients { get; init; } = new();
    public HashSet<string> AcknowledgedRecipients { get; init; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public bool IsCompleted => PendingRecipients.Count == 0;
}

public class BatchAcknowledgmentTracker
{
    private readonly ConcurrentDictionary<string, BatchAcknowledgmentSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly ITimer? _cleanupTimer;
    private readonly TimeSpan _sessionTimeout;
    private readonly TimeSpan _cleanupInterval;

    public BatchAcknowledgmentTracker(TimeProvider? timeProvider = null, TimeSpan? sessionTimeout = null, TimeSpan? cleanupInterval = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sessionTimeout = sessionTimeout ?? TimeSpan.FromMinutes(5);
        _cleanupInterval = cleanupInterval ?? TimeSpan.FromMinutes(1);

        // Only start periodic cleanup if using the real system clock.
        // Tests with a fake time provider should call CleanupExpiredSessions explicitly.
        if (_timeProvider == TimeProvider.System)
        {
            _cleanupTimer = _timeProvider.CreateTimer(CleanupExpiredSessions, null, _cleanupInterval, _cleanupInterval);
        }
    }

    public string CreateSession(string dataHash, string senderUid, IEnumerable<string> recipientUids)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var session = new BatchAcknowledgmentSession
        {
            SessionId = sessionId,
            DataHash = dataHash,
            SenderUid = senderUid,
            PendingRecipients = new HashSet<string>(recipientUids, StringComparer.Ordinal),
            CreatedAt = _timeProvider.GetUtcNow().DateTime
        };

        _sessions.TryAdd(sessionId, session);
        return sessionId;
    }

    public bool TryAcknowledge(string sessionId, string recipientUid, out BatchAcknowledgmentSession? session)
    {
        session = null;
        
        if (!_sessions.TryGetValue(sessionId, out session))
        {
            return false;
        }

        lock (session)
        {
            if (!session.PendingRecipients.Remove(recipientUid))
            {
                // Recipient was not in pending list (already acknowledged or not part of session)
                return false;
            }

            session.AcknowledgedRecipients.Add(recipientUid);
        }

        return true;
    }

    public bool TryAcknowledgeByHash(string dataHash, string recipientUid, out BatchAcknowledgmentSession? session)
    {
        session = null;

        var best = (BatchAcknowledgmentSession?)null;
        foreach (var kvp in _sessions)
        {
            var candidate = kvp.Value;
            if (!string.Equals(candidate.DataHash, dataHash, StringComparison.Ordinal))
            {
                continue;
            }

            lock (candidate)
            {
                if (!candidate.PendingRecipients.Contains(recipientUid))
                {
                    continue;
                }
            }

            if (best == null || candidate.CreatedAt > best.CreatedAt)
            {
                best = candidate;
            }
        }

        if (best == null)
        {
            return false;
        }

        return TryAcknowledge(best.SessionId, recipientUid, out session);
    }

    public bool IsSessionCompleted(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            return session.IsCompleted;
        }
        return false;
    }

    public void CompleteSession(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }

    public void CleanupSessionsForUser(string userUid)
    {
        var sessionsToRemove = new List<string>();
        
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.SenderUid == userUid)
            {
                sessionsToRemove.Add(kvp.Key);
            }
        }

        foreach (var sessionId in sessionsToRemove)
        {
            _sessions.TryRemove(sessionId, out _);
        }
    }

    public void CleanupExpiredSessions(object? state = null)
    {
        var expiredSessions = new List<string>();
        var cutoffTime = _timeProvider.GetUtcNow().DateTime - _sessionTimeout;

        foreach (var kvp in _sessions)
        {
            if (kvp.Value.CreatedAt < cutoffTime)
            {
                expiredSessions.Add(kvp.Key);
            }
        }

        foreach (var sessionId in expiredSessions)
        {
            _sessions.TryRemove(sessionId, out _);
        }
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }
}

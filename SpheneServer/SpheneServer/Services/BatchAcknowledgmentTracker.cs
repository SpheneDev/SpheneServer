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
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _sessionTimeout = TimeSpan.FromMinutes(5);

    public BatchAcknowledgmentTracker()
    {
        // Cleanup expired sessions every minute
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
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
            CreatedAt = DateTime.UtcNow
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

    private void CleanupExpiredSessions(object? state)
    {
        var expiredSessions = new List<string>();
        var cutoffTime = DateTime.UtcNow - _sessionTimeout;

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
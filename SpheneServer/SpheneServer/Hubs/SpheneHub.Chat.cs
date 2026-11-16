using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.User;
using SpheneServer.Utils;
using SpheneShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace SpheneServer.Hubs;

public partial class SpheneHub
{
    /// <summary>Uncompressed P-256 point length - the only accepted device key form.</summary>
    private const int ChatDevicePublicKeyLength = 65;
    /// <summary>Bound on a single HPKE copy blob (text stays under ~1 KiB).</summary>
    private const int MaxChatCipherBlobBytes = 4096;
    /// <summary>Recipient + sender devices both get copies: 8 + 8 worst case.</summary>
    private const int MaxChatCipherCopies = 16;

    [Authorize(Policy = "Identified")]
    public async Task UserBlock(UserDto dto)
    {
        var blockedUid = dto.User.UID;
        if (string.IsNullOrWhiteSpace(blockedUid)
            || string.Equals(blockedUid, UserUID, StringComparison.Ordinal))
        {
            return;
        }
        var exists = await DbContext.UserBlocks.AsNoTracking()
            .AnyAsync(b => b.UserUID == UserUID && b.BlockedUserUID == blockedUid)
            .ConfigureAwait(false);
        if (exists)
        {
            return;
        }
        var user = await DbContext.Users.SingleAsync(u => u.UID == UserUID)
            .ConfigureAwait(false);
        var blockedUser = await DbContext.Users.SingleOrDefaultAsync(
            u => u.UID == blockedUid).ConfigureAwait(false);
        if (blockedUser == null)
        {
            return;
        }
        DbContext.UserBlocks.Add(new UserBlock
        {
            User = user,
            UserUID = UserUID,
            BlockedUser = blockedUser,
            BlockedUserUID = blockedUid,
            BlockedOn = DateTime.UtcNow,
        });
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task UserUnblock(UserDto dto)
    {
        var entry = await DbContext.UserBlocks
            .SingleOrDefaultAsync(b => b.UserUID == UserUID
                && b.BlockedUserUID == dto.User.UID)
            .ConfigureAwait(false);
        if (entry == null)
        {
            return;
        }
        DbContext.UserBlocks.Remove(entry);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<UserDto>> UserGetBlockedUsers()
    {
        return await DbContext.UserBlocks.AsNoTracking()
            .Where(b => b.UserUID == UserUID)
            .Select(b => new UserDto(b.BlockedUser.ToUserData()))
            .ToListAsync().ConfigureAwait(false);
    }

    /// <summary>Blocking in either direction kills contact · used by pair
    /// requests and chat sends.</summary>
    private async Task<bool> IsBlockedEitherWay(string otherUid)
        => await DbContext.UserBlocks.AsNoTracking()
            .AnyAsync(b => (b.UserUID == UserUID && b.BlockedUserUID == otherUid)
                || (b.UserUID == otherUid && b.BlockedUserUID == UserUID))
            .ConfigureAwait(false);

    /// <summary>True when the caller may message <paramref name="otherUid"/>:
    /// exists, not self, paired and not blocked either way.</summary>
    private async Task<bool> CanMessageAsync(string otherUid, bool warnIfUnpaired)
    {
        if (string.IsNullOrWhiteSpace(otherUid)
            || string.Equals(otherUid, UserUID, StringComparison.Ordinal))
        {
            return false;
        }
        var exists = await DbContext.Users.AsNoTracking()
            .AnyAsync(u => u.UID == otherUid).ConfigureAwait(false);
        if (!exists || await IsBlockedEitherWay(otherUid).ConfigureAwait(false))
        {
            return false;
        }
        var paired = await DbContext.ClientPairs.AsNoTracking()
            .AnyAsync(p => p.UserUID == UserUID && p.OtherUserUID == otherUid)
            .ConfigureAwait(false);
        if (!paired && warnIfUnpaired)
        {
            await Clients.Caller.Client_ReceiveServerMessage(
                MessageSeverity.Warning, $"You can only message paired users ({otherUid})")
                .ConfigureAwait(false);
        }
        return paired;
    }

    [Authorize(Policy = "Identified")]
    public async Task UserSendChatMessage(UserDto recipient, string message)
    {
        var toUid = recipient.User.UID;
        var text = message.Trim();
        if (text.Length == 0 || text.Length > 500)
        {
            return;
        }
        if (!await CanMessageAsync(toUid, warnIfUnpaired: true).ConfigureAwait(false))
        {
            return;
        }
        // Downgrade guard: a recipient with active E2EE devices must never be
        // sent plaintext - a stale or modified client cannot silently weaken
        // new messages to them. The global flag hard-locks plaintext sends
        // for everyone once the fleet is on sealed chat.
        var recipientSealed = _requireE2EEChat
            || await DbContext.ChatDeviceKeys.AsNoTracking()
                .AnyAsync(k => k.UserUID == toUid && k.RevokedAtUtc == null)
                .ConfigureAwait(false);
        if (recipientSealed)
        {
            await Clients.Caller.Client_ReceiveServerMessage(
                MessageSeverity.Warning,
                _requireE2EEChat
                    ? "Plaintext chat is disabled on this server. Please update your Sphene client."
                    : $"{toUid} uses end-to-end encrypted chat. Please update your Sphene client to message them.")
                .ConfigureAwait(false);
            return;
        }
        var sender = await DbContext.Users.SingleAsync(u => u.UID == UserUID)
            .ConfigureAwait(false);
        var otherUser = await DbContext.Users.SingleAsync(u => u.UID == toUid)
            .ConfigureAwait(false);
        var entry = new ChatMessage
        {
            Id = Guid.NewGuid(),
            Sender = sender,
            SenderUID = UserUID,
            Recipient = otherUser,
            RecipientUID = toUid,
            Message = text,
            Kind = (byte)ChatMessageKind.Plaintext,
            SentAt = DateTime.UtcNow,
        };
        DbContext.ChatMessages.Add(entry);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        var dto = new ChatMessageDto(entry.Id, sender.ToUserData(),
            otherUser.ToUserData(), text, entry.SentAt, false);
        await Clients.User(toUid).Client_UserReceiveChatMessage(dto)
            .ConfigureAwait(false);
        await Clients.Caller.Client_UserReceiveChatMessage(dto)
            .ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task UserSendSealedChatMessage(UserDto recipient, Guid messageId,
        List<ChatCipherCopyDto> copies, DateTime sentAtUtc)
    {
        var toUid = recipient.User.UID;
        // The timestamp is bound into the AEAD metadata - the stored row must
        // carry the sender's sealed value verbatim or recipients cannot open
        // the copies. Skew beyond 5 minutes is rejected as bogus.
        if (messageId == Guid.Empty
            || Math.Abs((sentAtUtc - DateTime.UtcNow).TotalMinutes) > 5
            || !await CanMessageAsync(toUid, warnIfUnpaired: true).ConfigureAwait(false))
        {
            return;
        }
        var devices = await LoadActiveDeviceKeysAsync([UserUID, toUid]).ConfigureAwait(false);
        if (!ValidateCopies(copies, toUid, devices,
                requireAllDevicesCovered: true))
        {
            await Clients.Caller.Client_ReceiveServerMessage(
                MessageSeverity.Warning,
                "Encrypted message could not be delivered - the recipient's "
                + "encryption keys changed. Try sending again.")
                .ConfigureAwait(false);
            return;
        }
        // Idempotent resend: the client chose the id; a duplicate means the
        // first delivery already happened (or raced) - never duplicate rows.
        var exists = await DbContext.ChatMessages.AsNoTracking()
            .AnyAsync(m => m.Id == messageId).ConfigureAwait(false);
        if (exists)
        {
            return;
        }
        var sender = await DbContext.Users.SingleAsync(u => u.UID == UserUID)
            .ConfigureAwait(false);
        var otherUser = await DbContext.Users.SingleAsync(u => u.UID == toUid)
            .ConfigureAwait(false);
        var entry = new ChatMessage
        {
            Id = messageId,
            Sender = sender,
            SenderUID = UserUID,
            Recipient = otherUser,
            RecipientUID = toUid,
            Message = null,
            Kind = (byte)ChatMessageKind.SealedNative,
            SentAt = DateTime.SpecifyKind(sentAtUtc, DateTimeKind.Utc),
            Ciphers = copies.Select(ToCipherEntity).ToList(),
        };
        DbContext.ChatMessages.Add(entry);
        try
        {
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Lost an id race - treat like a duplicate send.
            return;
        }
        await DeliverSealedCopiesAsync(entry, copies).ConfigureAwait(false);
    }

    /// <summary>Pushes the accepted row to the recipient and the sender's own
    /// session - each side only gets the copies addressed to its devices.</summary>
    private async Task DeliverSealedCopiesAsync(ChatMessage entry,
        List<ChatCipherCopyDto> copies)
    {
        var senderData = entry.Sender.ToUserData();
        var recipientData = entry.Recipient.ToUserData();
        await Clients.User(entry.RecipientUID).Client_UserReceiveChatMessage(
            new ChatMessageDto(entry.Id, senderData, recipientData, string.Empty,
                entry.SentAt, false)
            {
                Kind = ChatMessageKind.SealedNative,
                Copies = CopiesFor(copies, entry.RecipientUID),
            }).ConfigureAwait(false);
        await Clients.User(UserUID).Client_UserReceiveChatMessage(
            new ChatMessageDto(entry.Id, senderData, recipientData, string.Empty,
                entry.SentAt, false)
            {
                Kind = ChatMessageKind.SealedNative,
                Copies = CopiesFor(copies, UserUID),
            }).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<ChatMessageDto>> UserGetChatHistory(
        UserDto other, int skip = 0, int take = 50)
    {
        var otherUid = other.User.UID;
        var rows = await DbContext.ChatMessages.AsNoTracking()
            .Include(m => m.Sender)
            .Include(m => m.Recipient)
            .Where(m => (m.SenderUID == UserUID && m.RecipientUID == otherUid)
                || (m.SenderUID == otherUid && m.RecipientUID == UserUID))
            .OrderByDescending(m => m.SentAt)
            .Skip(skip).Take(Math.Clamp(take, 1, 100))
            .ToListAsync().ConfigureAwait(false);
        var sealedIds = rows.Where(r => r.Kind != (byte)ChatMessageKind.Plaintext)
            .Select(r => r.Id).ToList();
        var ciphers = sealedIds.Count == 0 ? [] : await DbContext.ChatMessageCiphers
            .AsNoTracking()
            .Where(c => sealedIds.Contains(c.MessageId) && c.ForUID == UserUID)
            .ToListAsync().ConfigureAwait(false);
        var byMessage = ciphers.GroupBy(c => c.MessageId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ChatMessageCipher>)g.ToList());
        return rows.Select(m =>
        {
            var kind = (ChatMessageKind)m.Kind;
            var dto = new ChatMessageDto(m.Id, m.Sender.ToUserData(),
                m.Recipient.ToUserData(),
                kind == ChatMessageKind.Plaintext ? m.Message ?? string.Empty : string.Empty,
                m.SentAt, m.ReadAt != null)
            {
                Kind = kind,
                MigratedByUid = m.MigratedByUID,
            };
            if (kind != ChatMessageKind.Plaintext
                && byMessage.TryGetValue(m.Id, out var list))
            {
                dto.Copies = list.Select(ToCipherDto).ToList();
            }
            return dto;
        }).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task UserMarkChatRead(UserDto other)
    {
        var unread = await DbContext.ChatMessages
            .Where(m => m.SenderUID == other.User.UID
                && m.RecipientUID == UserUID && m.ReadAt == null)
            .ToListAsync().ConfigureAwait(false);
        if (unread.Count == 0)
        {
            return;
        }
        var now = DateTime.UtcNow;
        foreach (var m in unread)
        {
            m.ReadAt = now;
        }
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task UserPublishChatDeviceKey(string deviceId, byte[] publicKey)
    {
        deviceId = deviceId?.Trim() ?? string.Empty;
        if (deviceId.Length == 0 || deviceId.Length > 64 || publicKey == null
            || publicKey.Length != ChatDevicePublicKeyLength || publicKey[0] != 0x04)
        {
            return;
        }
        var user = await DbContext.Users.SingleAsync(u => u.UID == UserUID)
            .ConfigureAwait(false);
        var existing = await DbContext.ChatDeviceKeys
            .SingleOrDefaultAsync(k => k.UserUID == UserUID && k.DeviceId == deviceId)
            .ConfigureAwait(false);
        var now = DateTime.UtcNow;
        if (existing == null)
        {
            var active = await DbContext.ChatDeviceKeys
                .CountAsync(k => k.UserUID == UserUID && k.RevokedAtUtc == null)
                .ConfigureAwait(false);
            if (active >= _chatE2EEMaxDevices)
            {
                await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning,
                    "Too many chat encryption devices registered - revoke an old device first.")
                    .ConfigureAwait(false);
                return;
            }
            DbContext.ChatDeviceKeys.Add(new ChatDeviceKey
            {
                User = user,
                UserUID = UserUID,
                DeviceId = deviceId,
                PublicKey = publicKey,
                PublishedAtUtc = now,
            });
        }
        else
        {
            // Re-publish (reconnect, or key material reset under a stable id):
            // resurrect the device and pin the latest key. Clients detect the
            // material change through their key-cache comparison.
            existing.RevokedAtUtc = null;
            existing.PublicKey = publicKey;
            existing.PublishedAtUtc = now;
        }
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task UserRevokeChatDeviceKey(string deviceId)
    {
        var key = await DbContext.ChatDeviceKeys
            .SingleOrDefaultAsync(k => k.UserUID == UserUID && k.DeviceId == deviceId
                && k.RevokedAtUtc == null)
            .ConfigureAwait(false);
        if (key == null)
        {
            return;
        }
        key.RevokedAtUtc = DateTime.UtcNow;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<ChatDeviceKeyDto>> UserGetChatDeviceKeys(List<string> uids)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { UserUID };
        var others = (uids ?? [])
            .Where(u => !string.IsNullOrWhiteSpace(u)
                && !string.Equals(u, UserUID, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Take(50).ToList();
        if (others.Count > 0)
        {
            var paired = await DbContext.ClientPairs.AsNoTracking()
                .Where(p => p.UserUID == UserUID && others.Contains(p.OtherUserUID))
                .Select(p => p.OtherUserUID).ToListAsync().ConfigureAwait(false);
            var blocked = await DbContext.UserBlocks.AsNoTracking()
                .Where(b => (b.UserUID == UserUID && others.Contains(b.BlockedUserUID))
                    || (others.Contains(b.UserUID) && b.BlockedUserUID == UserUID))
                .Select(b => b.UserUID == UserUID ? b.BlockedUserUID : b.UserUID)
                .ToListAsync().ConfigureAwait(false);
            var blockedSet = new HashSet<string>(blocked, StringComparer.Ordinal);
            foreach (var uid in paired)
            {
                if (!blockedSet.Contains(uid)) allowed.Add(uid);
            }
        }
        return await DbContext.ChatDeviceKeys.AsNoTracking()
            .Where(k => allowed.Contains(k.UserUID) && k.RevokedAtUtc == null)
            .Select(k => new ChatDeviceKeyDto(k.UserUID, k.DeviceId, k.PublicKey,
                k.PublishedAtUtc))
            .ToListAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> UserMigrateChatMessage(Guid messageId,
        List<ChatCipherCopyDto> copies)
    {
        var row = await DbContext.ChatMessages
            .SingleOrDefaultAsync(m => m.Id == messageId).ConfigureAwait(false);
        if (row == null
            || (!string.Equals(row.SenderUID, UserUID, StringComparison.Ordinal)
                && !string.Equals(row.RecipientUID, UserUID, StringComparison.Ordinal)))
        {
            return false;
        }
        // The context can hold a stale tracked copy (e.g. the caller composed
        // this plaintext row on the same connection) - refresh before judging
        // migration state, or a concurrent/earlier migration is invisible.
        await DbContext.Entry(row).ReloadAsync().ConfigureAwait(false);
        // Idempotent: already sealed natively or migrated by either party.
        if (row.Kind != (byte)ChatMessageKind.Plaintext)
        {
            return true;
        }
        var otherUid = string.Equals(row.SenderUID, UserUID, StringComparison.Ordinal)
            ? row.RecipientUID : row.SenderUID;
        var devices = await LoadActiveDeviceKeysAsync([UserUID, otherUid])
            .ConfigureAwait(false);
        // Migration only runs when BOTH participants can be given copies -
        // sealing solely for the migrator would lock the other user out.
        if (!ValidateCopies(copies, otherUid, devices,
                requireAllDevicesCovered: true))
        {
            return false;
        }
        row.Kind = (byte)ChatMessageKind.SealedMigrated;
        row.Message = null;
        row.MigratedAtUtc = DateTime.UtcNow;
        row.MigratedByUID = UserUID;
        foreach (var copy in copies)
        {
            var entity = ToCipherEntity(copy);
            entity.MessageId = row.Id;
            DbContext.ChatMessageCiphers.Add(entity);
        }
        try
        {
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent migration claimed the row first (cipher key
            // collision). The message is sealed either way - idempotent win.
            return await DbContext.ChatMessages.AsNoTracking()
                .AnyAsync(m => m.Id == messageId
                    && m.Kind != (byte)ChatMessageKind.Plaintext)
                .ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>Copies must address exactly the active devices of caller and
    /// <paramref name="otherUid"/> (full coverage so nobody loses access),
    /// be sealed by the caller's published device key and carry a v1 blob.</summary>
    private bool ValidateCopies(List<ChatCipherCopyDto> copies,
        string otherUid, Dictionary<string, Dictionary<string, byte[]>> devices,
        bool requireAllDevicesCovered)
    {
        if (copies == null || copies.Count == 0 || copies.Count > MaxChatCipherCopies)
        {
            return false;
        }
        var myDevices = devices.GetValueOrDefault(UserUID) ?? [];
        var theirDevices = devices.GetValueOrDefault(otherUid) ?? [];
        if (requireAllDevicesCovered && (myDevices.Count == 0 || theirDevices.Count == 0))
        {
            return false;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var coveredMine = new HashSet<string>(StringComparer.Ordinal);
        var coveredTheirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var copy in copies)
        {
            var forMine = string.Equals(copy.ForUid, UserUID, StringComparison.Ordinal);
            var forTheirs = string.Equals(copy.ForUid, otherUid, StringComparison.Ordinal);
            if (!forMine && !forTheirs) return false;
            var targetDevices = forMine ? myDevices : theirDevices;
            if (copy.ForDeviceId.Length == 0 || copy.ForDeviceId.Length > 64
                || !targetDevices.ContainsKey(copy.ForDeviceId))
            {
                return false;
            }
            if (!seen.Add(copy.ForUid + "|" + copy.ForDeviceId)) return false;
            if (!string.Equals(copy.SealerUid, UserUID, StringComparison.Ordinal)
                || !myDevices.TryGetValue(copy.SealerDeviceId, out var published)
                || !published.AsSpan().SequenceEqual(copy.SealerPublicKey))
            {
                return false;
            }
            if (copy.Blob == null || copy.Blob.Length < 1 + ChatDevicePublicKeyLength
                || copy.Blob.Length > MaxChatCipherBlobBytes || copy.Blob[0] != 0x01)
            {
                return false;
            }
            (forMine ? coveredMine : coveredTheirs).Add(copy.ForDeviceId);
        }
        return !requireAllDevicesCovered
            || (coveredMine.Count == myDevices.Count
                && coveredTheirs.Count == theirDevices.Count);
    }

    /// <summary>uid → deviceId → public key for every active device key.</summary>
    private async Task<Dictionary<string, Dictionary<string, byte[]>>> LoadActiveDeviceKeysAsync(
        IEnumerable<string> uids)
    {
        var keys = await DbContext.ChatDeviceKeys.AsNoTracking()
            .Where(k => uids.Contains(k.UserUID) && k.RevokedAtUtc == null)
            .ToListAsync().ConfigureAwait(false);
        var map = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!map.TryGetValue(key.UserUID, out var devices))
            {
                map[key.UserUID] = devices = new(StringComparer.Ordinal);
            }
            devices[key.DeviceId] = key.PublicKey;
        }
        return map;
    }

    private static List<ChatCipherCopyDto> CopiesFor(IEnumerable<ChatCipherCopyDto> copies,
        string uid)
        => copies.Where(c => string.Equals(c.ForUid, uid, StringComparison.Ordinal)).ToList();

    private static ChatMessageCipher ToCipherEntity(ChatCipherCopyDto copy) => new()
    {
        ForUID = copy.ForUid,
        ForDeviceId = copy.ForDeviceId,
        SealerUID = copy.SealerUid,
        SealerDeviceId = copy.SealerDeviceId,
        SealerPublicKey = copy.SealerPublicKey,
        Blob = copy.Blob,
    };

    private static ChatCipherCopyDto ToCipherDto(ChatMessageCipher row) => new()
    {
        ForUid = row.ForUID,
        ForDeviceId = row.ForDeviceId,
        SealerUid = row.SealerUID,
        SealerDeviceId = row.SealerDeviceId,
        SealerPublicKey = row.SealerPublicKey,
        Blob = row.Blob,
    };
}

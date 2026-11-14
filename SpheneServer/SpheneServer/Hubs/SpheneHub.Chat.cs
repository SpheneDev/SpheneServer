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

    [Authorize(Policy = "Identified")]
    public async Task UserSendChatMessage(UserDto recipient, string message)
    {
        var toUid = recipient.User.UID;
        var text = message.Trim();
        if (string.IsNullOrWhiteSpace(toUid) || text.Length == 0 || text.Length > 500
            || string.Equals(toUid, UserUID, StringComparison.Ordinal))
        {
            return;
        }
        var otherUser = await DbContext.Users.SingleOrDefaultAsync(
            u => u.UID == toUid).ConfigureAwait(false);
        if (otherUser == null || await IsBlockedEitherWay(toUid).ConfigureAwait(false))
        {
            return;
        }
        var paired = await DbContext.ClientPairs.AsNoTracking()
            .AnyAsync(p => p.UserUID == UserUID && p.OtherUserUID == toUid)
            .ConfigureAwait(false);
        if (!paired)
        {
            await Clients.Caller.Client_ReceiveServerMessage(
                MessageSeverity.Warning, $"You can only message paired users ({toUid})")
                .ConfigureAwait(false);
            return;
        }
        var sender = await DbContext.Users.SingleAsync(u => u.UID == UserUID)
            .ConfigureAwait(false);
        var entry = new ChatMessage
        {
            Id = Guid.NewGuid(),
            Sender = sender,
            SenderUID = UserUID,
            Recipient = otherUser,
            RecipientUID = toUid,
            Message = text,
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
    public async Task<List<ChatMessageDto>> UserGetChatHistory(
        UserDto other, int skip = 0, int take = 50)
    {
        var otherUid = other.User.UID;
        return await DbContext.ChatMessages.AsNoTracking()
            .Where(m => (m.SenderUID == UserUID && m.RecipientUID == otherUid)
                || (m.SenderUID == otherUid && m.RecipientUID == UserUID))
            .OrderByDescending(m => m.SentAt)
            .Skip(skip).Take(Math.Clamp(take, 1, 100))
            .Select(m => new ChatMessageDto(m.Id, m.Sender.ToUserData(),
                m.Recipient.ToUserData(), m.Message, m.SentAt, m.ReadAt != null))
            .ToListAsync().ConfigureAwait(false);
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
}

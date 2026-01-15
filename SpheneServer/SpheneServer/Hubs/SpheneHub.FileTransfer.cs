using Microsoft.EntityFrameworkCore;
using Sphene.API.Dto.Files;
using Sphene.API.Data;
using SpheneShared.Models;
using Microsoft.AspNetCore.SignalR;

namespace SpheneServer.Hubs;

public partial class SpheneHub
{
    private static string NormalizeHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length > 128)
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[hash.Length];
        var length = 0;
        foreach (var c in hash)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                continue;
            }

            buffer[length++] = char.ToUpperInvariant(c);
            if (length > 40)
            {
                return string.Empty;
            }
        }

        return length == 40 ? new string(buffer[..length]) : string.Empty;
    }

    private async Task CheckPendingFileTransfersAsync()
    {
        var pendingTransfers = await DbContext.PendingFileTransfers
            .AsNoTracking()
            .Where(p => p.RecipientUID == UserUID)
            .ToListAsync();

        if (!pendingTransfers.Any()) return;

        foreach (var transfer in pendingTransfers)
        {
            var notification = new FileTransferNotificationDto
            {
                Sender = new UserData(transfer.SenderUID, transfer.SenderAlias),
                Hash = transfer.Hash,
                ModFolderName = transfer.ModFolderName,
                ModInfo = transfer.ModInfo
            };

            await Clients.Caller.Client_UserReceiveFileNotification(notification);
        }
    }

    public async Task UserAckFileTransfer(string hash, string senderUid)
    {
        var normalizedHash = NormalizeHash(hash);
        if (string.IsNullOrEmpty(normalizedHash))
        {
            return;
        }

        senderUid ??= string.Empty;

        var pendingTransfers = await DbContext.PendingFileTransfers
            .Where(p => p.RecipientUID == UserUID
                && (string.IsNullOrEmpty(senderUid) || p.SenderUID == senderUid)
                && (p.Hash == normalizedHash || p.Hash.ToUpper() == normalizedHash))
            .ToListAsync();

        if (pendingTransfers.Any())
        {
            DbContext.PendingFileTransfers.RemoveRange(pendingTransfers);
            await DbContext.SaveChangesAsync();
        }
    }
}

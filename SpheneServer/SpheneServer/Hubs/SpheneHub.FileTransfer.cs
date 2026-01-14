using Microsoft.EntityFrameworkCore;
using Sphene.API.Dto.Files;
using Sphene.API.Data;
using SpheneShared.Models;
using Microsoft.AspNetCore.SignalR;

namespace SpheneServer.Hubs;

public partial class SpheneHub
{
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
        var pendingTransfers = await DbContext.PendingFileTransfers
            .Where(p => p.RecipientUID == UserUID && p.SenderUID == senderUid && p.Hash == hash)
            .ToListAsync();

        if (pendingTransfers.Any())
        {
            DbContext.PendingFileTransfers.RemoveRange(pendingTransfers);
            await DbContext.SaveChangesAsync();
        }
    }
}

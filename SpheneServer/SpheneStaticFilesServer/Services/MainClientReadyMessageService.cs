using Microsoft.AspNetCore.SignalR;
using Sphene.API.SignalR;
using SpheneServer.Hubs;

namespace SpheneStaticFilesServer.Services;

public class MainClientReadyMessageService : IClientReadyMessageService
{
    private readonly ILogger<MainClientReadyMessageService> _logger;
    private readonly IHubContext<SpheneHub> _spheneHub;

    public MainClientReadyMessageService(ILogger<MainClientReadyMessageService> logger, IHubContext<SpheneHub> spheneHub)
    {
        _logger = logger;
        _spheneHub = spheneHub;
    }

    public async Task SendDownloadReady(string uid, Guid requestId)
    {
        _logger.LogInformation("Sending Client Ready for {uid}:{requestId} to SignalR", uid, requestId);
        await _spheneHub.Clients.User(uid).SendAsync(nameof(ISpheneHub.Client_DownloadReady), requestId).ConfigureAwait(false);
    }
}

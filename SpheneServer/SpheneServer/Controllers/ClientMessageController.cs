using Sphene.API.SignalR;
using SpheneServer.Hubs;
using SpheneShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace SpheneServer.Controllers;

[Route("/msgc")]
[Authorize(Policy = "Internal")]
public class ClientMessageController : Controller
{
    private ILogger<ClientMessageController> _logger;
    private IHubContext<SpheneHub, ISpheneHub> _hubContext;

    public ClientMessageController(ILogger<ClientMessageController> logger, IHubContext<SpheneHub, ISpheneHub> hubContext)
    {
        _logger = logger;
        _hubContext = hubContext;
    }

    [Route("sendMessage")]
    [HttpPost]
    public async Task<IActionResult> SendMessage(ClientMessage msg)
    {
        bool hasUid = !string.IsNullOrEmpty(msg.UID);

        if (!hasUid)
        {
            _logger.LogInformation("Sending Message of severity {severity} to all online users: {message}", msg.Severity, msg.Message);
            await _hubContext.Clients.All.Client_ReceiveServerMessage(msg.Severity, msg.Message).ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation("Sending Message of severity {severity} to user {uid}: {message}", msg.Severity, msg.UID, msg.Message);
            await _hubContext.Clients.User(msg.UID).Client_ReceiveServerMessage(msg.Severity, msg.Message).ConfigureAwait(false);
        }

        return Empty;
    }
}

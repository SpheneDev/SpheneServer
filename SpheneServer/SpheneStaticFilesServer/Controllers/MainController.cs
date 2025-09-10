using SpheneFiles = Sphene.API.Routes.SpheneFiles;
using SpheneShared.Utils.Configuration;
using SpheneStaticFilesServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SpheneStaticFilesServer.Controllers;

[Route(SpheneFiles.Main)]
[Authorize(Policy = "Internal")]
public class MainController : ControllerBase
{
    private readonly IClientReadyMessageService _messageService;
    private readonly MainServerShardRegistrationService _shardRegistrationService;

    public MainController(ILogger<MainController> logger, IClientReadyMessageService SpheneHub,
        MainServerShardRegistrationService shardRegistrationService) : base(logger)
    {
        _messageService = SpheneHub;
        _shardRegistrationService = shardRegistrationService;
    }

    [HttpGet(SpheneFiles.Main_SendReady)]
    public async Task<IActionResult> SendReadyToClients(string uid, Guid requestId)
    {
        await _messageService.SendDownloadReady(uid, requestId).ConfigureAwait(false);
        return Ok();
    }

    [HttpPost("shardRegister")]
    public IActionResult RegisterShard([FromBody] ShardConfiguration shardConfiguration)
    {
        try
        {
            _shardRegistrationService.RegisterShard(SpheneUser, shardConfiguration);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shard could not be registered {shard}", SpheneUser);
            return BadRequest();
        }
    }

    [HttpPost("shardUnregister")]
    public IActionResult UnregisterShard()
    {
        _shardRegistrationService.UnregisterShard(SpheneUser);
        return Ok();
    }

    [HttpPost("shardHeartbeat")]
    public IActionResult ShardHeartbeat()
    {
        try
        {
            _shardRegistrationService.ShardHeartbeat(SpheneUser);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shard not registered: {shard}", SpheneUser);
            return BadRequest();
        }
    }
}

using Microsoft.AspNetCore.Mvc;
using SpheneServices.Kofi;

namespace SpheneServices.Controllers;

[Route("/kofi")]
public class KofiWebhookController : Controller
{
    private readonly ILogger<KofiWebhookController> _logger;
    private readonly KofiWebhookService _kofiService;

    public KofiWebhookController(ILogger<KofiWebhookController> logger, KofiWebhookService kofiService)
    {
        _logger = logger;
        _kofiService = kofiService;
    }

    [Route("webhook")]
    [HttpPost]
    public async Task<IActionResult> Webhook()
    {
        var form = await Request.ReadFormAsync().ConfigureAwait(false);
        if (!form.TryGetValue("data", out var dataValues))
        {
            _logger.LogWarning("Ko-fi webhook received without 'data' field");
            return BadRequest("Missing 'data' field");
        }

        var rawData = dataValues.ToString();
        if (string.IsNullOrWhiteSpace(rawData))
        {
            _logger.LogWarning("Ko-fi webhook received with empty 'data' field");
            return BadRequest("Empty 'data' field");
        }

        _logger.LogDebug("Ko-fi webhook received, processing payload");

        try
        {
            var success = await _kofiService.ProcessWebhookAsync(rawData).ConfigureAwait(false);
            if (success)
                return Ok();
            else
                return BadRequest("Failed to process webhook");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Ko-fi webhook");
            return StatusCode(500, "Internal server error");
        }
    }
}

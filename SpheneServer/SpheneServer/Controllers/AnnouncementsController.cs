using Sphene.API.Dto.Announcement;
using SpheneServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SpheneServer.Controllers;

/// <summary>
/// REST surface for announcements · writes share the hub's
/// <see cref="AnnouncementWriter"/> so validation and client broadcasts are
/// identical whether an article was authored in-game or by an internal tool.
/// The anonymous GET feeds public consumers (website listing); writes require
/// the "Internal" policy claim like <c>/msgc</c>.
/// </summary>
[Route("/announcements")]
public class AnnouncementsController : Controller
{
    private readonly ILogger<AnnouncementsController> _logger;
    private readonly AnnouncementWriter _writer;

    public AnnouncementsController(ILogger<AnnouncementsController> logger,
        AnnouncementWriter writer)
    {
        _logger = logger;
        _writer = writer;
    }

    /// <summary>Public JSON list of published announcements · for the website.</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> List([FromQuery] int skip = 0, [FromQuery] int take = 20)
    {
        var items = await _writer.ListAsync(skip, take).ConfigureAwait(false);
        return Json(items);
    }

    /// <summary>Creates an announcement; non-drafts broadcast to all clients.</summary>
    [HttpPost("create")]
    [Authorize(Policy = "Internal")]
    public async Task<IActionResult> Create([FromBody] AnnouncementUpsertDto dto,
        [FromQuery] string authorUid = "system", [FromQuery] string authorDisplay = "Sphene Team")
    {
        if (!AnnouncementWriter.TryValidate(dto, out var error))
        {
            _logger.LogInformation("Rejected announcement create via REST: {error}", error);
            return BadRequest(error);
        }

        var id = await _writer.CreateAsync(authorUid, authorDisplay, dto).ConfigureAwait(false);
        return Json(id);
    }

    /// <summary>Updates an announcement; a first publish broadcasts to all clients.</summary>
    [HttpPut("update")]
    [Authorize(Policy = "Internal")]
    public async Task<IActionResult> Update([FromBody] AnnouncementUpsertDto dto,
        [FromQuery] string authorUid = "system")
    {
        var error = string.Empty;
        if (dto == null || !AnnouncementWriter.TryValidate(dto, out error))
        {
            _logger.LogInformation("Rejected announcement update via REST: {error}", dto == null ? "empty" : error);
            return BadRequest(dto == null ? "Empty payload" : error);
        }

        var updated = await _writer.UpdateAsync(authorUid, dto).ConfigureAwait(false);
        return updated ? Json(true) : NotFound();
    }

    /// <summary>Soft-deletes an announcement and broadcasts the change.</summary>
    [HttpDelete("{id:long}")]
    [Authorize(Policy = "Internal")]
    public async Task<IActionResult> Delete(long id, [FromQuery] string authorUid = "system")
    {
        var deleted = await _writer.DeleteAsync(authorUid, id).ConfigureAwait(false);
        return deleted ? Empty : NotFound();
    }
}

using SpheneFiles = Sphene.API.Routes.SpheneFiles;
using SpheneStaticFilesServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SpheneStaticFilesServer.Controllers;

[Route(SpheneFiles.Distribution)]
public class DistributionController : ControllerBase
{
    private readonly CachedFileProvider _cachedFileProvider;

    public DistributionController(ILogger<DistributionController> logger, CachedFileProvider cachedFileProvider) : base(logger)
    {
        _cachedFileProvider = cachedFileProvider;
    }

    [HttpGet(SpheneFiles.Distribution_Get)]
    [Authorize(Policy = "Internal")]
    public async Task<IActionResult> GetFile(string file)
    {
        _logger.LogInformation($"GetFile:{SpheneUser}:{file}");

        var fs = await _cachedFileProvider.DownloadAndGetLocalFileInfo(file);
        if (fs == null) return NotFound();

        return PhysicalFile(fs.FullName, "application/octet-stream");
    }
}

using SpheneShared.Utils;
using Microsoft.AspNetCore.Mvc;

namespace SpheneStaticFilesServer.Controllers;

public class ControllerBase : Controller
{
    protected ILogger _logger;

    public ControllerBase(ILogger logger)
    {
        _logger = logger;
    }

    protected string SpheneUser => HttpContext.User.Claims.First(f => string.Equals(f.Type, SpheneClaimTypes.Uid, StringComparison.Ordinal)).Value;
    protected string Continent => HttpContext.User.Claims.FirstOrDefault(f => string.Equals(f.Type, SpheneClaimTypes.Continent, StringComparison.Ordinal))?.Value ?? "*";
    protected bool IsPriority => !string.IsNullOrEmpty(HttpContext.User.Claims.FirstOrDefault(f => string.Equals(f.Type, SpheneClaimTypes.Alias, StringComparison.Ordinal))?.Value ?? string.Empty);
}

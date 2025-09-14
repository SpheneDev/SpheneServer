using SpheneShared.Utils.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SpheneShared.Services;

[Route("configuration/[controller]")]
[Authorize(Policy = "Internal")]
public class SpheneConfigurationController<T> : Controller where T : class, ISpheneConfiguration
{
    private readonly ILogger<SpheneConfigurationController<T>> _logger;
    private IOptionsMonitor<T> _config;

    public SpheneConfigurationController(IOptionsMonitor<T> config, ILogger<SpheneConfigurationController<T>> logger)
    {
        _config = config;
        _logger = logger;
    }

    [HttpGet("GetConfigurationEntry")]
    [Authorize(Policy = "Internal")]
    public IActionResult GetConfigurationEntry(string key, string defaultValue)
    {
        var result = _config.CurrentValue.SerializeValue(key, defaultValue);
        _logger.LogInformation("Requested " + key + ", returning:" + result);
        return Ok(result);
    }
}

#pragma warning disable MA0048 // File name must match type name
public class SpheneStaticFilesServerConfigurationController : SpheneConfigurationController<StaticFilesServerConfiguration>
{
    public SpheneStaticFilesServerConfigurationController(IOptionsMonitor<StaticFilesServerConfiguration> config, ILogger<SpheneStaticFilesServerConfigurationController> logger) : base(config, logger)
    {
    }
}

public class SpheneBaseConfigurationController : SpheneConfigurationController<SpheneConfigurationBase>
{
    public SpheneBaseConfigurationController(IOptionsMonitor<SpheneConfigurationBase> config, ILogger<SpheneBaseConfigurationController> logger) : base(config, logger)
    {
    }
}

public class SpheneServerConfigurationController : SpheneConfigurationController<ServerConfiguration>
{
    public SpheneServerConfigurationController(IOptionsMonitor<ServerConfiguration> config, ILogger<SpheneServerConfigurationController> logger) : base(config, logger)
    {
    }
}

public class SpheneServicesConfigurationController : SpheneConfigurationController<ServicesConfiguration>
{
    public SpheneServicesConfigurationController(IOptionsMonitor<ServicesConfiguration> config, ILogger<SpheneServicesConfigurationController> logger) : base(config, logger)
    {
    }
}
#pragma warning restore MA0048 // File name must match type name

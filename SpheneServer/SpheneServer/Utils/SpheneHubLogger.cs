using SpheneServer.Hubs;
using System.Runtime.CompilerServices;

namespace SpheneServer.Utils;

public class SpheneHubLogger
{
    private readonly SpheneHub _hub;
    private readonly ILogger<SpheneHub> _logger;

    public SpheneHubLogger(SpheneHub hub, ILogger<SpheneHub> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public static object[] Args(params object[] args)
    {
        return args;
    }

    public void LogCallInfo(object[] args = null, [CallerMemberName] string methodName = "")
    {
        string formattedArgs = args != null && args.Length != 0 ? "|" + string.Join(":", args) : string.Empty;
        _logger.LogInformation("{uid}:{method}{args}", _hub.UserUID, methodName, formattedArgs);
    }

    public void LogCallWarning(object[] args = null, [CallerMemberName] string methodName = "")
    {
        string formattedArgs = args != null && args.Length != 0 ? "|" + string.Join(":", args) : string.Empty;
        _logger.LogWarning("{uid}:{method}{args}", _hub.UserUID, methodName, formattedArgs);
    }

    public void LogDebug(string message, params object[] args)
    {
        _logger.LogDebug(message, args);
    }
}

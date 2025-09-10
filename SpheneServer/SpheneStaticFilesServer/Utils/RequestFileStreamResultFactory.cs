using SpheneShared.Metrics;
using SpheneShared.Services;
using SpheneShared.Utils.Configuration;
using SpheneStaticFilesServer.Services;

namespace SpheneStaticFilesServer.Utils;

public class RequestFileStreamResultFactory
{
    private readonly SpheneMetrics _metrics;
    private readonly RequestQueueService _requestQueueService;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configurationService;

    public RequestFileStreamResultFactory(SpheneMetrics metrics, RequestQueueService requestQueueService, IConfigurationService<StaticFilesServerConfiguration> configurationService)
    {
        _metrics = metrics;
        _requestQueueService = requestQueueService;
        _configurationService = configurationService;
    }

    public RequestFileStreamResult Create(Guid requestId, Stream stream)
    {
        return new RequestFileStreamResult(requestId, _requestQueueService,
            _metrics, stream, "application/octet-stream");
    }
}

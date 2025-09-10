using SpheneShared.Metrics;
using SpheneStaticFilesServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace SpheneStaticFilesServer.Utils;

public class RequestFileStreamResult : FileStreamResult
{
    private readonly Guid _requestId;
    private readonly RequestQueueService _requestQueueService;
    private readonly SpheneMetrics _SpheneMetrics;

    public RequestFileStreamResult(Guid requestId, RequestQueueService requestQueueService, SpheneMetrics SpheneMetrics,
        Stream fileStream, string contentType) : base(fileStream, contentType)
    {
        _requestId = requestId;
        _requestQueueService = requestQueueService;
        _SpheneMetrics = SpheneMetrics;
        _SpheneMetrics.IncGauge(MetricsAPI.GaugeCurrentDownloads);
    }

    public override void ExecuteResult(ActionContext context)
    {
        try
        {
            base.ExecuteResult(context);
        }
        catch
        {
            throw;
        }
        finally
        {
            _requestQueueService.FinishRequest(_requestId);

            _SpheneMetrics.DecGauge(MetricsAPI.GaugeCurrentDownloads);
            FileStream?.Dispose();
        }
    }

    public override async Task ExecuteResultAsync(ActionContext context)
    {
        try
        {
            await base.ExecuteResultAsync(context).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }
        finally
        {
            _requestQueueService.FinishRequest(_requestId);
            _SpheneMetrics.DecGauge(MetricsAPI.GaugeCurrentDownloads);
            FileStream?.Dispose();
        }
    }
}

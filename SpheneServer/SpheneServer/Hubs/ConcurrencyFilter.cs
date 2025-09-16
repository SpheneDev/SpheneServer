using SpheneShared.Metrics;
using SpheneShared.Services;
using SpheneShared.Utils.Configuration;
using Microsoft.AspNetCore.SignalR;
using System.Threading.RateLimiting;

namespace SpheneServer.Hubs;

public sealed class ConcurrencyFilter : IHubFilter, IDisposable
{
    private ConcurrencyLimiter _limiter;
    private int _setLimit = 0;
    private readonly IConfigurationService<ServerConfiguration> _config;
    private readonly CancellationTokenSource _cts = new();

    private bool _disposed;

    public ConcurrencyFilter(IConfigurationService<ServerConfiguration> config, SpheneMetrics SpheneMetrics)
    {
        _config = config;
        _config.ConfigChangedEvent += OnConfigChange;

        RecreateLimiter();

        _ = Task.Run(async () =>
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                var stats = _limiter?.GetStatistics();
                if (stats != null)
                {
                    SpheneMetrics.SetGaugeTo(MetricsAPI.GaugeHubConcurrency, stats.CurrentAvailablePermits);
                    SpheneMetrics.SetGaugeTo(MetricsAPI.GaugeHubQueuedConcurrency, stats.CurrentQueuedCount);
                }
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        });
    }

    private void OnConfigChange(object sender, EventArgs e)
    {
        RecreateLimiter();
    }

    private void RecreateLimiter()
    {
        var newLimit = _config.GetValueOrDefault(nameof(ServerConfiguration.HubExecutionConcurrencyFilter), 50);

        if (newLimit == _setLimit && _limiter is not null)
        {
            return;
        }

        _setLimit = newLimit;
        _limiter?.Dispose();
        _limiter = new(new ConcurrencyLimiterOptions()
        {
            PermitLimit = newLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = newLimit * 100,
        });
    }

    public async ValueTask<object?> InvokeMethodAsync(
    HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (string.Equals(invocationContext.HubMethodName, nameof(SpheneHub.CheckClientHealth), StringComparison.Ordinal))
        {
            return await next(invocationContext).ConfigureAwait(false);
        }

        // Create a timeout cancellation token (30 seconds for hub methods)
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            invocationContext.Context.ConnectionAborted, 
            timeoutCts.Token);
        
        var lease = await _limiter.AcquireAsync(1, combinedCts.Token).ConfigureAwait(false);
        
        try
        {
            if (!lease.IsAcquired)
            {
                throw new HubException("Concurrency limit exceeded. Try again later.");
            }
            
            return await next(invocationContext).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new HubException($"Hub method '{invocationContext.HubMethodName}' timed out after 30 seconds.");
        }
        catch (OperationCanceledException ex) when (invocationContext.Context.ConnectionAborted.IsCancellationRequested)
        {
            throw new TaskCanceledException("Operation was cancelled due to connection closure.", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new TaskCanceledException("Operation was cancelled.", ex);
        }
        finally
        {
            lease.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _limiter?.Dispose();
        _config.ConfigChangedEvent -= OnConfigChange;
        _cts.Dispose();
    }
}

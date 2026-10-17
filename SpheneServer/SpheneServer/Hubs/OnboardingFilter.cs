using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SpheneShared.Data;
using SpheneShared.Utils;

namespace SpheneServer.Hubs;

/// <summary>
/// Blocks all hub invocations for users who have not completed identity setup (no alias set).
/// Only the allowlisted onboarding methods stay reachable.
/// </summary>
public sealed class OnboardingFilter : IHubFilter
{
    internal const string OnboardedItemKey = "SpheneOnboarded";

    private static readonly HashSet<string> _allowedMethods = new(StringComparer.Ordinal)
    {
        nameof(SpheneHub.GetConnectionDto),
        nameof(SpheneHub.CheckClientHealth),
        nameof(SpheneHub.UserCheckAliasAvailability),
        nameof(SpheneHub.UserSetAlias),
    };

    private readonly IDbContextFactory<SpheneDbContext> _dbContextFactory;
    private readonly ILogger<OnboardingFilter> _logger;

    public OnboardingFilter(IDbContextFactory<SpheneDbContext> dbContextFactory, ILogger<OnboardingFilter> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async ValueTask<object> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object>> next)
    {
        if (_allowedMethods.Contains(invocationContext.HubMethodName))
        {
            return await next(invocationContext).ConfigureAwait(false);
        }

        if (await IsOnboardedAsync(invocationContext).ConfigureAwait(false))
        {
            return await next(invocationContext).ConfigureAwait(false);
        }

        var uid = invocationContext.Context.User?.Claims?.SingleOrDefault(c => string.Equals(c.Type, SpheneClaimTypes.Uid, StringComparison.Ordinal))?.Value ?? "Unknown";
        _logger.LogWarning("Blocked hub call {method} from {uid}: identity setup not completed", invocationContext.HubMethodName, uid);
        throw new HubException("Identity setup required. Please set your alias in the Sphene client (update required if no setup window appeared).");
    }

    private async Task<bool> IsOnboardedAsync(HubInvocationContext invocationContext)
    {
        if (invocationContext.Context.Items.TryGetValue(OnboardedItemKey, out var cached) && cached is bool cachedValue)
        {
            return cachedValue;
        }

        var uid = invocationContext.Context.User?.Claims?.SingleOrDefault(c => string.Equals(c.Type, SpheneClaimTypes.Uid, StringComparison.Ordinal))?.Value;
        if (uid == null)
        {
            return true;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var onboarded = await db.Users.AsNoTracking()
            .Where(u => u.UID == uid)
            .Select(u => !string.IsNullOrEmpty(u.Alias))
            .SingleOrDefaultAsync().ConfigureAwait(false);
        invocationContext.Context.Items[OnboardedItemKey] = onboarded;
        return onboarded;
    }
}

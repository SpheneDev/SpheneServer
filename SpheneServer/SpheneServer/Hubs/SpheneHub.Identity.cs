using Npgsql;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto;
using Sphene.API.Dto.User;
using SpheneServer.Utils;
using SpheneShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace SpheneServer.Hubs;

public partial class SpheneHub
{
    private static readonly Regex _aliasValidationRegex = new(Constants.AliasValidationPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _displayNameControlCharsRegex = new(@"\p{C}+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly TimeSpan _aliasRenameCooldown = TimeSpan.FromDays(Constants.AliasRenameCooldownDays);

    private static string NormalizeAlias(string? alias)
    {
        return alias?.Trim() ?? string.Empty;
    }

    private bool IsAliasFormatValid(string alias)
    {
        return _aliasValidationRegex.IsMatch(alias);
    }

    private async Task<bool> AliasExistsAsync(string alias)
    {
        var lowered = alias.ToLowerInvariant();
        return await DbContext.Users.AnyAsync(u =>
            (u.Alias != null && u.Alias.ToLower() == lowered && u.UID != UserUID)
            || u.UID.ToLower() == lowered).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<AliasAvailabilityResult> UserCheckAliasAvailability(string alias)
    {
        _logger.LogCallInfo();

        var normalized = NormalizeAlias(alias);
        if (!IsAliasFormatValid(normalized)) return AliasAvailabilityResult.Invalid;
        if (await AliasExistsAsync(normalized).ConfigureAwait(false)) return AliasAvailabilityResult.Taken;
        return AliasAvailabilityResult.Available;
    }

    [Authorize(Policy = "Identified")]
    public async Task<SetAliasResult> UserSetAlias(string alias)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(alias));

        var user = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(user.Alias)) return SetAliasResult.AlreadySet;

        var normalized = NormalizeAlias(alias);
        if (!IsAliasFormatValid(normalized)) return SetAliasResult.Invalid;
        if (await AliasExistsAsync(normalized).ConfigureAwait(false)) return SetAliasResult.Taken;

        user.Alias = normalized;
        try
        {
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return SetAliasResult.Taken;
        }

        Context.Items[OnboardingFilter.OnboardedItemKey] = true;

        _logger.LogCallInfo(SpheneHubLogger.Args("Alias set", normalized));
        return SetAliasResult.Success;
    }

    [Authorize(Policy = "Identified")]
    public async Task<SetAliasResult> UserRenameAlias(string newAlias)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(newAlias));

        var user = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        if (string.IsNullOrEmpty(user.Alias)) return SetAliasResult.Invalid;

        var normalized = NormalizeAlias(newAlias);
        if (!IsAliasFormatValid(normalized)) return SetAliasResult.Invalid;
        if (string.Equals(user.Alias, normalized, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(user.Alias, normalized, StringComparison.Ordinal))
            {
                user.Alias = normalized;
                await DbContext.SaveChangesAsync().ConfigureAwait(false);
                await PushIdentityUpdateToPairedUsersAsync(user).ConfigureAwait(false);
            }
            return SetAliasResult.Success;
        }

        if (user.AliasChangedAt.HasValue && DateTime.UtcNow - user.AliasChangedAt.Value < _aliasRenameCooldown)
        {
            return SetAliasResult.Cooldown;
        }
        if (await AliasExistsAsync(normalized).ConfigureAwait(false)) return SetAliasResult.Taken;

        user.Alias = normalized;
        user.AliasChangedAt = DateTime.UtcNow;
        try
        {
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return SetAliasResult.Taken;
        }

        _logger.LogCallInfo(SpheneHubLogger.Args("Alias renamed", normalized));
        await PushIdentityUpdateToPairedUsersAsync(user).ConfigureAwait(false);
        return SetAliasResult.Success;
    }

    [Authorize(Policy = "Identified")]
    public async Task UserSetDisplayName(string? displayName)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(displayName));

        var user = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        var normalized = NormalizeDisplayName(displayName);
        if (string.Equals(user.DisplayName, normalized, StringComparison.Ordinal)) return;

        user.DisplayName = normalized;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        await PushIdentityUpdateToPairedUsersAsync(user).ConfigureAwait(false);
    }

    private static string? NormalizeDisplayName(string? displayName)
    {
        var trimmed = _displayNameControlCharsRegex.Replace(displayName?.Trim() ?? string.Empty, string.Empty);
        if (trimmed.Length > Constants.DisplayNameMaxLength)
        {
            trimmed = trimmed[..Constants.DisplayNameMaxLength];
        }
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Resolves a user-supplied identifier (UID or alias, case-insensitive) to the real UID.
    /// Returns null when no matching user exists.
    /// </summary>
    private async Task<string?> ResolveUidOrAliasAsync(string? uidOrAlias)
    {
        var trimmed = uidOrAlias?.Trim().TrimStart('@');
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (string.Equals(trimmed, UserUID, StringComparison.Ordinal)) return UserUID;

        var lowered = trimmed.ToLowerInvariant();
        return await DbContext.Users
            .Where(u => u.UID == trimmed || u.Alias != null && u.Alias.ToLower() == lowered)
            .Select(u => u.UID)
            .SingleOrDefaultAsync().ConfigureAwait(false);
    }

    private async Task PushIdentityUpdateToPairedUsersAsync(User user)
    {
        var pairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        if (pairedUsers.Count == 0) return;

        var dto = new UserDto(new UserData(user.UID, user.Alias, Utils.Extensions.IsSupporterEffective(user), user.DisplayName));
        await Clients.Users(pairedUsers).Client_UserIdentityUpdated(dto).ConfigureAwait(false);
    }
}

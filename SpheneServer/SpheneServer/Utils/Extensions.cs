using System.Text.Json;
using System.Text.Json.Nodes;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using SpheneShared.Models;
using static SpheneServer.Hubs.SpheneHub;

namespace SpheneServer.Utils;

public static class Extensions
{
    /// <summary>Window of the one-time supporter trial.</summary>
    public static readonly TimeSpan SupporterTrialDuration = TimeSpan.FromHours(24);

    /// <summary>Effective supporter flag - paid status or an active
    /// (not yet expired) trial · used everywhere another user's perk
    /// visibility is decided, never for the paid-only checks.</summary>
    public static bool IsSupporterEffective(User user)
        => IsSupporterEffective(user.IsSupporter, user.SupporterTrialStartUtc);

    /// <summary>Primitive overload for projected/anonymous rows.</summary>
    public static bool IsSupporterEffective(bool isSupporter, DateTime? trialStartUtc)
        => isSupporter || (trialStartUtc.HasValue
            && DateTime.UtcNow - trialStartUtc.Value < SupporterTrialDuration);

    /// <summary>Profile-theme keys gated behind supporter status - list-row
    /// effects beyond the always-on baseline plus the card fx set. The
    /// baseline bits (Glow+Pulse) are re-applied by the client's parse
    /// anyway, so dropping the keys lands every row on the free look.</summary>
    private static readonly string[] SupporterThemeKeys =
    [
        "rowHover", "rowHoverFx", "rowIdle", "rowIdleFx",
        "cardHover", "cardHoverFx", "cardIdle", "cardIdleFx", "cardShimmer",
    ];

    /// <summary>Strips supporter-gated styling keys from a profile theme
    /// when the owner isn't an effective supporter, so a modified client
    /// cannot stash perk visuals server-side. Free appearance fields
    /// (accent, banner, over-flags) ride through untouched; unparseable
    /// input is returned as-is - the client-side parse falls back to
    /// defaults anyway.</summary>
    public static string? NormalizeThemeForSupporter(string? themeJson, bool effectiveSupporter)
    {
        if (effectiveSupporter || string.IsNullOrWhiteSpace(themeJson)) return themeJson;
        try
        {
            if (JsonNode.Parse(themeJson) is not JsonObject theme) return themeJson;
            foreach (var key in SupporterThemeKeys) theme.Remove(key);
            return theme.Count == 0 ? string.Empty : theme.ToJsonString();
        }
        catch (JsonException)
        {
            return themeJson;
        }
    }

    public static GroupData ToGroupData(this Group group)
    {
        return new GroupData(group.GID, group.Alias);
    }

    public static UserData ToUserData(this GroupPair pair)
    {
        return new UserData(pair.GroupUser.UID, pair.GroupUser.Alias, IsSupporterEffective(pair.GroupUser), pair.GroupUser.DisplayName);
    }

    public static UserData ToUserData(this User user)
    {
        return new UserData(user.UID, user.Alias, IsSupporterEffective(user), user.DisplayName);
    }

    public static IndividualPairStatus ToIndividualPairStatus(this UserInfo userInfo)
    {
        if (userInfo.IndividuallyPaired) return IndividualPairStatus.Bidirectional;
        if (!userInfo.IndividuallyPaired && userInfo.GIDs.Contains(Constants.IndividualKeyword, StringComparer.Ordinal)) return IndividualPairStatus.OneSided;
        return IndividualPairStatus.None;
    }

    public static GroupPermissions ToEnum(this Group group)
    {
        var permissions = GroupPermissions.NoneSet;
        permissions.SetPreferDisableAnimations(group.PreferDisableAnimations);
        permissions.SetPreferDisableSounds(group.PreferDisableSounds);
        permissions.SetPreferDisableVFX(group.PreferDisableVFX);
        permissions.SetDisableInvites(!group.InvitesEnabled);
        return permissions;
    }

    public static GroupUserPreferredPermissions ToEnum(this GroupPairPreferredPermission groupPair)
    {
        var permissions = GroupUserPreferredPermissions.NoneSet;
        permissions.SetDisableAnimations(groupPair.DisableAnimations);
        permissions.SetDisableSounds(groupPair.DisableSounds);
        permissions.SetPaused(groupPair.IsPaused);
        permissions.SetDisableVFX(groupPair.DisableVFX);
        return permissions;
    }

    public static GroupPairUserInfo ToEnum(this GroupPair groupPair)
    {
        var groupUserInfo = GroupPairUserInfo.None;
        groupUserInfo.SetPinned(groupPair.IsPinned);
        groupUserInfo.SetModerator(groupPair.IsModerator);
        return groupUserInfo;
    }

    public static UserPermissions ToUserPermissions(this UserPermissionSet? permissions, bool setSticky = false)
    {
        if (permissions == null) return UserPermissions.NoneSet;

        UserPermissions perm = UserPermissions.NoneSet;
        perm.SetPaused(permissions.IsPaused);
        perm.SetDisableAnimations(permissions.DisableAnimations);
        perm.SetDisableSounds(permissions.DisableSounds);
        perm.SetDisableVFX(permissions.DisableVFX);
        perm.SetDisableVFXInDuty(permissions.DisableVFXInDuty);
        perm.SetAckYou(permissions.AckYou);
        if (setSticky)
            perm.SetSticky(permissions.Sticky);
        return perm;
    }
}

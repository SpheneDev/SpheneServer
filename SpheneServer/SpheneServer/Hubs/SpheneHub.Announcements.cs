using Sphene.API.Dto.Announcement;
using SpheneServer.Services;
using SpheneServer.Utils;
using SpheneShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace SpheneServer.Hubs;

public partial class SpheneHub
{
    /// <summary>
    /// One page of published announcements for the news surface · pinned
    /// first, then newest. Drafts and soft-deleted rows never leave the
    /// server.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<List<AnnouncementDto>> GetAnnouncements(int skip = 0, int take = 20)
    {
        _logger.LogCallInfo();
        var page = await _announcementWriter.ListAsync(skip, take).ConfigureAwait(false);
        return [.. page];
    }

    /// <summary>
    /// Staff view of every live announcement · drafts included so the
    /// in-plugin editor can manage them. Non-staff callers get an empty list.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<List<AnnouncementDto>> AdminGetAnnouncements(int skip = 0, int take = 50)
    {
        _logger.LogCallInfo();
        var user = await GetStaffUser().ConfigureAwait(false);
        if (user == null)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("Rejected", "not-staff"));
            return [];
        }
        var page = await _announcementWriter.ListAllAsync(skip, take).ConfigureAwait(false);
        return [.. page];
    }

    /// <summary>
    /// Staff-only create · returns the new id, or null when the caller is not
    /// staff or the payload fails validation.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<long?> AdminCreateAnnouncement(AnnouncementUpsertDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto?.Title));
        var user = await GetStaffUser().ConfigureAwait(false);
        if (user == null)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("Rejected", "not-staff"));
            return null;
        }
        var error = string.Empty;
        if (dto == null || !AnnouncementWriter.TryValidate(dto, out error))
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("Rejected", dto == null ? "empty" : error));
            return null;
        }

        return await _announcementWriter.CreateAsync(UserUID, StaffDisplayName(user), dto)
            .ConfigureAwait(false);
    }

    /// <summary>Staff-only update · a first publish broadcasts the publish event.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> AdminUpdateAnnouncement(AnnouncementUpsertDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto?.Id, dto?.Title));
        var user = await GetStaffUser().ConfigureAwait(false);
        if (user == null)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("Rejected", "not-staff"));
            return false;
        }
        var error = string.Empty;
        if (dto == null || !AnnouncementWriter.TryValidate(dto, out error))
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("Rejected", dto == null ? "empty" : error));
            return false;
        }

        return await _announcementWriter.UpdateAsync(UserUID, dto).ConfigureAwait(false);
    }

    /// <summary>Staff-only soft delete · broadcasts the change to every client.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> AdminDeleteAnnouncement(long id)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(id));
        var user = await GetStaffUser().ConfigureAwait(false);
        if (user == null)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("Rejected", "not-staff"));
            return false;
        }

        return await _announcementWriter.DeleteAsync(UserUID, id).ConfigureAwait(false);
    }

    /// <summary>Caller as a staff user row · null when not admin or moderator.</summary>
    private async Task<User> GetStaffUser()
    {
        var user = await DbContext.Users
            .SingleOrDefaultAsync(u => u.UID == UserUID).ConfigureAwait(false);
        return user is { IsAdmin: true } or { IsModerator: true } ? user : null;
    }

    private static string StaffDisplayName(User user)
        => !string.IsNullOrWhiteSpace(user.DisplayName) ? user.DisplayName
            : !string.IsNullOrWhiteSpace(user.Alias) ? user.Alias : "Sphene Team";
}

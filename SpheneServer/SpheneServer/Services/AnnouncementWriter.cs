using Sphene.API.Dto.Announcement;
using Sphene.API.SignalR;
using SpheneServer.Hubs;
using SpheneShared.Data;
using SpheneShared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace SpheneServer.Services;

/// <summary>
/// Single write path for announcements · the hub admin methods and the
/// internal REST controller both validate and persist through this service so
/// validation rules, the publish-push contract (drafts never broadcast) and
/// the change broadcasts stay identical no matter which surface authored
/// the row.
/// </summary>
public class AnnouncementWriter
{
    /// <summary>Hard cap for one page of announcements.</summary>
    public const int MaxTake = 50;

    public const string DefaultCategory = "news";

    private static readonly HashSet<string> AllowedCategories = new(StringComparer.Ordinal)
        { "news", "event", "maintenance", "patch" };

    private readonly IDbContextFactory<SpheneDbContext> _dbContextFactory;
    private readonly IHubContext<SpheneHub, ISpheneHub> _hubContext;
    private readonly ILogger<AnnouncementWriter> _logger;

    public AnnouncementWriter(IDbContextFactory<SpheneDbContext> dbContextFactory,
        IHubContext<SpheneHub, ISpheneHub> hubContext, ILogger<AnnouncementWriter> logger)
    {
        _dbContextFactory = dbContextFactory;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>Validates an upsert · returns false with a reason on failure.</summary>
    public static bool TryValidate(AnnouncementUpsertDto dto, out string error)
    {
        error = string.Empty;
        if (dto == null || string.IsNullOrWhiteSpace(dto.Title))
        {
            error = "Title is required";
            return false;
        }
        if (dto.Title.Trim().Length > 200)
        {
            error = "Title exceeds 200 characters";
            return false;
        }
        if ((dto.Summary ?? string.Empty).Length > 500)
        {
            error = "Summary exceeds 500 characters";
            return false;
        }
        if ((dto.Body ?? string.Empty).Length > 16000)
        {
            error = "Body exceeds 16000 characters";
            return false;
        }
        var category = (dto.Category ?? string.Empty).Trim().ToLowerInvariant();
        if (category.Length > 0 && !AllowedCategories.Contains(category))
        {
            error = "Unknown category";
            return false;
        }
        if (!IsValidUrl(dto.BannerImageUrl) || !IsValidUrl(dto.LinkUrl))
        {
            error = "URLs must start with http:// or https://";
            return false;
        }
        return true;
    }

    /// <summary>Reads one published page · pinned first, then newest.</summary>
    public async Task<IReadOnlyList<AnnouncementDto>> ListAsync(int skip, int take)
    {
        take = Math.Clamp(take, 1, MaxTake);
        skip = Math.Max(0, skip);
        using var db = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var rows = await db.Announcements.AsNoTracking()
            .Where(a => a.DeletedAt == null && !a.IsDraft)
            .OrderByDescending(a => a.IsPinned)
            .ThenByDescending(a => a.PublishedAt)
            .Skip(skip).Take(take)
            .ToListAsync().ConfigureAwait(false);
        return rows.Select(ToDto).ToList();
    }

    /// <summary>
    /// Staff view of every live row · drafts included, pinned first, then by
    /// last touch. Callers must gate on admin/moderator before invoking.
    /// </summary>
    public async Task<IReadOnlyList<AnnouncementDto>> ListAllAsync(int skip, int take)
    {
        take = Math.Clamp(take, 1, MaxTake);
        skip = Math.Max(0, skip);
        using var db = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var rows = await db.Announcements.AsNoTracking()
            .Where(a => a.DeletedAt == null)
            .OrderByDescending(a => a.IsPinned)
            .ThenByDescending(a => a.UpdatedAt)
            .Skip(skip).Take(take)
            .ToListAsync().ConfigureAwait(false);
        return rows.Select(ToDto).ToList();
    }

    /// <summary>Inserts a row; broadcasts <c>Client_AnnouncementPublished</c> only for non-drafts.</summary>
    public async Task<long> CreateAsync(string authorUid, string authorDisplay, AnnouncementUpsertDto dto)
    {
        var now = DateTime.UtcNow;
        var entity = new Announcement
        {
            Title = dto.Title.Trim(),
            Summary = (dto.Summary ?? string.Empty).Trim(),
            Body = (dto.Body ?? string.Empty).Trim(),
            Category = NormalizeCategory(dto.Category),
            BannerImageUrl = NullIfEmpty(dto.BannerImageUrl),
            LinkUrl = NullIfEmpty(dto.LinkUrl),
            AuthorUid = (authorUid ?? string.Empty).Trim(),
            AuthorDisplay = string.IsNullOrWhiteSpace(authorDisplay) ? "Sphene Team" : authorDisplay.Trim(),
            IsPinned = dto.IsPinned,
            IsDraft = dto.IsDraft,
            PublishedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        using var db = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        db.Announcements.Add(entity);
        await db.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogInformation("Announcement {id} created by {uid} (draft={draft}, pinned={pinned}, title={title})",
            entity.Id, authorUid, entity.IsDraft, entity.IsPinned, entity.Title);

        if (!entity.IsDraft)
        {
            await _hubContext.Clients.All.Client_AnnouncementPublished(ToDto(entity)).ConfigureAwait(false);
        }
        return entity.Id;
    }

    /// <summary>
    /// Updates a row · a draft becoming visible for the first time restamps
    /// <c>PublishedAt</c> and broadcasts the publish event; every other edit
    /// just broadcasts <c>Client_AnnouncementsChanged</c>.
    /// </summary>
    public async Task<bool> UpdateAsync(string actorUid, AnnouncementUpsertDto dto)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var entity = await db.Announcements
            .SingleOrDefaultAsync(a => a.Id == dto.Id && a.DeletedAt == null).ConfigureAwait(false);
        if (entity == null)
        {
            return false;
        }

        var wasDraft = entity.IsDraft;
        entity.Title = dto.Title.Trim();
        entity.Summary = (dto.Summary ?? string.Empty).Trim();
        entity.Body = (dto.Body ?? string.Empty).Trim();
        entity.Category = NormalizeCategory(dto.Category);
        entity.BannerImageUrl = NullIfEmpty(dto.BannerImageUrl);
        entity.LinkUrl = NullIfEmpty(dto.LinkUrl);
        entity.IsPinned = dto.IsPinned;
        entity.IsDraft = dto.IsDraft;
        entity.UpdatedAt = DateTime.UtcNow;
        if (wasDraft && !entity.IsDraft)
        {
            entity.PublishedAt = entity.UpdatedAt;
        }
        await db.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogInformation("Announcement {id} updated by {uid} (draft={draft}, wasDraft={wasDraft})",
            entity.Id, actorUid, entity.IsDraft, wasDraft);

        if (wasDraft && !entity.IsDraft)
        {
            await _hubContext.Clients.All.Client_AnnouncementPublished(ToDto(entity)).ConfigureAwait(false);
        }
        else
        {
            await _hubContext.Clients.All.Client_AnnouncementsChanged().ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>Soft-deletes a row and broadcasts the change.</summary>
    public async Task<bool> DeleteAsync(string actorUid, long id)
    {
        using var db = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var entity = await db.Announcements
            .SingleOrDefaultAsync(a => a.Id == id && a.DeletedAt == null).ConfigureAwait(false);
        if (entity == null)
        {
            return false;
        }

        entity.DeletedAt = DateTime.UtcNow;
        entity.UpdatedAt = entity.DeletedAt.Value;
        await db.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogInformation("Announcement {id} deleted by {uid}", id, actorUid);
        await _hubContext.Clients.All.Client_AnnouncementsChanged().ConfigureAwait(false);
        return true;
    }

    public static AnnouncementDto ToDto(Announcement a)
        => new(a.Id, a.Title, a.Summary, a.Body, a.Category,
            a.BannerImageUrl, a.LinkUrl, a.AuthorDisplay, a.PublishedAt,
            a.IsPinned, a.IsDraft);

    private static string NormalizeCategory(string category)
    {
        var c = (category ?? string.Empty).Trim().ToLowerInvariant();
        return AllowedCategories.Contains(c) ? c : DefaultCategory;
    }

    private static bool IsValidUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        if (url.Trim().Length > 500) return false;
        return url.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    private static string NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

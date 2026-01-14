using K4os.Compression.LZ4.Legacy;
using SpheneFiles = Sphene.API.Routes.SpheneFiles;
using Sphene.API.Data;
using Sphene.API.SignalR;
using SpheneServer.Hubs;
using SpheneShared.Data;
using SpheneShared.Metrics;
using SpheneShared.Models;
using SpheneShared.Services;
using SpheneShared.Utils.Configuration;
using SpheneStaticFilesServer.Services;
using SpheneStaticFilesServer.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Sphene.API.Dto.Files;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpheneStaticFilesServer.Controllers;

[Route(SpheneFiles.ServerFiles)]
public class ServerFilesController : ControllerBase
{
    private const long MaxUploadRequestSize = 1024L * 1024 * 1024;
    private static readonly SemaphoreSlim _fileLockDictLock = new(1);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileUploadLocks = new(StringComparer.Ordinal);
    private readonly string _basePath;
    private readonly CachedFileProvider _cachedFileProvider;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;
    private readonly IHubContext<SpheneHub> _hubContext;
    private readonly IDbContextFactory<SpheneDbContext> _SpheneDbContext;
    private readonly SpheneMetrics _metricsClient;
    private readonly MainServerShardRegistrationService _shardRegistrationService;

    public ServerFilesController(ILogger<ServerFilesController> logger, CachedFileProvider cachedFileProvider,
        IConfigurationService<StaticFilesServerConfiguration> configuration,
        IHubContext<SpheneHub> hubContext,
        IDbContextFactory<SpheneDbContext> SpheneDbContext, SpheneMetrics metricsClient,
        MainServerShardRegistrationService shardRegistrationService) : base(logger)
    {
        _basePath = configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false)
            ? configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.ColdStorageDirectory))
            : configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
        _cachedFileProvider = cachedFileProvider;
        _configuration = configuration;
        _hubContext = hubContext;
        _SpheneDbContext = SpheneDbContext;
        _metricsClient = metricsClient;
        _shardRegistrationService = shardRegistrationService;
    }

    [HttpPost(SpheneFiles.ServerFiles_DeleteAll)]
    public async Task<IActionResult> FilesDeleteAll()
    {
        using var dbContext = await _SpheneDbContext.CreateDbContextAsync();
        var ownFiles = await dbContext.Files.Where(f => f.Uploaded && f.Uploader.UID == SpheneUser).ToListAsync().ConfigureAwait(false);
        bool isColdStorage = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false);

        foreach (var dbFile in ownFiles)
        {
            var fi = FilePathUtil.GetFileInfoForHash(_basePath, dbFile.Hash);
            if (fi != null)
            {
                _metricsClient.DecGauge(isColdStorage ? MetricsAPI.GaugeFilesTotalColdStorage : MetricsAPI.GaugeFilesTotal, fi == null ? 0 : 1);
                _metricsClient.DecGauge(isColdStorage ? MetricsAPI.GaugeFilesTotalSizeColdStorage : MetricsAPI.GaugeFilesTotalSize, fi?.Length ?? 0);

                fi?.Delete();
            }
        }

        dbContext.Files.RemoveRange(ownFiles);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        return Ok();
    }

    [HttpGet(SpheneFiles.ServerFiles_GetSizes)]
    public async Task<IActionResult> FilesGetSizes([FromBody] List<string> hashes)
    {
        using var dbContext = await _SpheneDbContext.CreateDbContextAsync();
        var forbiddenFiles = await dbContext.ForbiddenUploadEntries.
            Where(f => hashes.Contains(f.Hash)).ToListAsync().ConfigureAwait(false);
        List<DownloadFileDto> response = new();

        var cacheFile = await dbContext.Files.AsNoTracking()
            .Where(f => hashes.Contains(f.Hash))
            .Select(k => new { k.Hash, k.Size, k.RawSize })
            .ToListAsync().ConfigureAwait(false);

        var allFileShards = _shardRegistrationService.GetConfigurationsByContinent(Continent);

        foreach (var file in cacheFile)
        {
            var forbiddenFile = forbiddenFiles.SingleOrDefault(f => string.Equals(f.Hash, file.Hash, StringComparison.OrdinalIgnoreCase));
            Uri? baseUrl = null;

            if (forbiddenFile == null)
            {
                var matchingShards = allFileShards.Where(f => new Regex(f.FileMatch).IsMatch(file.Hash)).ToList();

                var shard = matchingShards.SelectMany(g => g.RegionUris)
                    .OrderBy(g => Guid.NewGuid()).FirstOrDefault();

                baseUrl = shard.Value ?? _configuration.GetValue<Uri>(nameof(StaticFilesServerConfiguration.CdnFullUrl));
            }

            response.Add(new DownloadFileDto
            {
                FileExists = file.Size > 0,
                ForbiddenBy = forbiddenFile?.ForbiddenBy ?? string.Empty,
                IsForbidden = forbiddenFile != null,
                Hash = file.Hash,
                Size = file.Size,
                Url = baseUrl?.ToString() ?? string.Empty,
                RawSize = file.RawSize
            });
        }

        return Ok(JsonSerializer.Serialize(response));
    }

    [HttpGet(SpheneFiles.ServerFiles_DownloadServers)]
    public async Task<IActionResult> GetDownloadServers()
    {
        var allFileShards = _shardRegistrationService.GetConfigurationsByContinent(Continent);
        return Ok(JsonSerializer.Serialize(allFileShards.Where(t => t.RegionUris != null).SelectMany(t => t.RegionUris.Where(v => v.Value != null).Select(v => v.Value.ToString()))));
    }

    [HttpPost(SpheneFiles.ServerFiles_FilesSend)]
    public async Task<IActionResult> FilesSend([FromBody] FilesSendDto filesSendDto)
    {
        using var dbContext = await _SpheneDbContext.CreateDbContextAsync();

        var userSentHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hash in filesSendDto.FileHashes ?? [])
        {
            var normalizedHash = NormalizeHashForLookup(hash);
            if (string.IsNullOrEmpty(normalizedHash))
            {
                continue;
            }

            userSentHashes.Add(normalizedHash);
        }
        _logger.LogInformation("{user}|FilesSend: requested upload for {hashCount} hashes to {uidCount} recipients", SpheneUser, userSentHashes.Count, filesSendDto.UIDs?.Count ?? 0);
        
        if (filesSendDto.ModInfo != null)
        {
             _logger.LogInformation("FilesSend: Received {count} ModInfo entries.", filesSendDto.ModInfo.Count);
        }
        else
        {
             _logger.LogInformation("FilesSend: No ModInfo received (null).");
        }

        var notCoveredFiles = new Dictionary<string, UploadFileDto>(StringComparer.Ordinal);
        var forbiddenFiles = await dbContext.ForbiddenUploadEntries.AsNoTracking().Where(f => userSentHashes.Contains(f.Hash)).ToDictionaryAsync(f => f.Hash, f => f).ConfigureAwait(false);
        var existingFiles = await dbContext.Files.AsNoTracking().Where(f => userSentHashes.Contains(f.Hash)).ToDictionaryAsync(f => f.Hash, f => f).ConfigureAwait(false);
        var unavailableHashes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var existingFile in existingFiles)
        {
            if (!existingFile.Value.Uploaded || existingFile.Value.Size <= 0)
            {
                continue;
            }

            FileInfo? fileInfo;
            try
            {
                fileInfo = FilePathUtil.GetFileInfoForHash(_basePath, existingFile.Key);
            }
            catch
            {
                unavailableHashes.Add(existingFile.Key);
                continue;
            }

            if (fileInfo == null || !fileInfo.Exists || fileInfo.Length != existingFile.Value.Size)
            {
                unavailableHashes.Add(existingFile.Key);
            }
        }

        if (unavailableHashes.Count > 0)
        {
            try
            {
                bool isColdStorage = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false);

                var affectedFiles = await dbContext.Files
                    .Where(f => unavailableHashes.Contains(f.Hash) && f.Uploaded)
                    .ToListAsync(HttpContext.RequestAborted)
                    .ConfigureAwait(false);

                foreach (var dbFile in affectedFiles)
                {
                    if (dbFile.Size > 0)
                    {
                        _metricsClient.DecGauge(isColdStorage ? MetricsAPI.GaugeFilesTotalColdStorage : MetricsAPI.GaugeFilesTotal, 1);
                        _metricsClient.DecGauge(isColdStorage ? MetricsAPI.GaugeFilesTotalSizeColdStorage : MetricsAPI.GaugeFilesTotalSize, dbFile.Size);
                    }

                    dbFile.Uploaded = false;
                    dbFile.Size = 0;
                    dbFile.RawSize = 0;
                }

                var staleTransfers = await dbContext.PendingFileTransfers
                    .Where(p => unavailableHashes.Contains(p.Hash))
                    .ToListAsync(HttpContext.RequestAborted)
                    .ConfigureAwait(false);

                if (staleTransfers.Count > 0)
                {
                    dbContext.PendingFileTransfers.RemoveRange(staleTransfers);
                }

                if (affectedFiles.Count > 0 || staleTransfers.Count > 0)
                {
                    await dbContext.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{user}|FilesSend: Failed to clean up unavailable hashes", SpheneUser);
            }
        }

        if (filesSendDto.ModInfo != null && filesSendDto.ModInfo.Any())
        {
            foreach (var modInfo in filesSendDto.ModInfo)
            {
                var existingMod = await dbContext.ModFiles.FindAsync(modInfo.Hash);
                if (existingMod == null)
                {
                    _logger.LogInformation("Adding new ModFile to DB: {hash}", modInfo.Hash);
                    await dbContext.ModFiles.AddAsync(new ModFile
                    {
                        Hash = modInfo.Hash,
                        Name = modInfo.Name,
                        Author = modInfo.Author,
                        Version = modInfo.Version,
                        Description = modInfo.Description,
                        Website = modInfo.Website,
                        UploadedDate = DateTime.UtcNow
                    });
                }
                else
                {
                     _logger.LogInformation("ModFile already exists for {hash}", modInfo.Hash);
                }
            }
            try 
            {
                var changes = await dbContext.SaveChangesAsync().ConfigureAwait(false);
                _logger.LogInformation("Saved {changes} changes to ModFiles table.", changes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving ModFiles to database.");
            }
        }

        foreach (var hash in userSentHashes)
        {
            // Skip empty file hashes, duplicate file hashes, forbidden file hashes and existing file hashes
            if (string.IsNullOrEmpty(hash)) { continue; }
            if (notCoveredFiles.ContainsKey(hash)) { continue; }
            if (forbiddenFiles.ContainsKey(hash))
            {
                notCoveredFiles[hash] = new UploadFileDto()
                {
                    ForbiddenBy = forbiddenFiles[hash].ForbiddenBy,
                    Hash = hash,
                    IsForbidden = true,
                };

                continue;
            }
            if (unavailableHashes.Contains(hash))
            {
                notCoveredFiles[hash] = new UploadFileDto()
                {
                    Hash = hash,
                };

                continue;
            }
            if (existingFiles.TryGetValue(hash, out var file) && file.Uploaded) { continue; }

            notCoveredFiles[hash] = new UploadFileDto()
            {
                Hash = hash,
            };
        }

        var hashesForNotification = userSentHashes
            .Where(h => !string.IsNullOrEmpty(h)
                        && !forbiddenFiles.ContainsKey(h)
                        && !unavailableHashes.Contains(h)
                        && existingFiles.TryGetValue(h, out var file)
                        && file.Uploaded)
            .ToList();

        var recipientUids = filesSendDto.UIDs?.Distinct(StringComparer.Ordinal).ToList() ?? new List<string>();

        if (hashesForNotification.Any() && recipientUids.Any())
        {
            var isPenumbraModUpload = filesSendDto.ModFolderNames != null && filesSendDto.ModFolderNames.Count > 0;
            Dictionary<string, string>? modFolderNamesLookup = null;
            if (filesSendDto.ModFolderNames != null && filesSendDto.ModFolderNames.Count > 0)
            {
                modFolderNamesLookup = new Dictionary<string, string>(filesSendDto.ModFolderNames, StringComparer.OrdinalIgnoreCase);
            }

            if (isPenumbraModUpload)
            {
                recipientUids = await GetBidirectionalIndividualRecipientsAsync(dbContext, SpheneUser, recipientUids).ConfigureAwait(false);
            }

            if (recipientUids.Any())
            {
                if (!isPenumbraModUpload)
                {
                    await _hubContext.Clients.Users(recipientUids)
                        .SendAsync(nameof(ISpheneHub.Client_UserReceiveUploadStatus), new Sphene.API.Dto.User.UserDto(new(SpheneUser)))
                        .ConfigureAwait(false);
                }

                var senderAlias = string.IsNullOrWhiteSpace(SpheneAlias) ? null : SpheneAlias;
                var sender = new Sphene.API.Data.UserData(SpheneUser, senderAlias);
                var pendingTransfers = new List<PendingFileTransfer>();

                foreach (var uid in recipientUids)
                {
                    foreach (var singleHash in hashesForNotification)
                    {
                        string? modFolderName = null;
                        if (modFolderNamesLookup != null && modFolderNamesLookup.Count > 0)
                        {
                            modFolderNamesLookup.TryGetValue(singleHash, out modFolderName);
                        }

                        List<ModInfoDto>? singleModInfo = null;
                        if (filesSendDto.ModInfo != null && filesSendDto.ModInfo.Count > 0)
                        {
                            var match = filesSendDto.ModInfo.FirstOrDefault(m => string.Equals(m.Hash, singleHash, StringComparison.OrdinalIgnoreCase));
                            if (match != null)
                            {
                                singleModInfo = new List<ModInfoDto>(1) { match };
                            }
                        }

                        var notification = new FileTransferNotificationDto
                        {
                            Sender = sender,
                            Recipient = new Sphene.API.Data.UserData(uid),
                            Hash = singleHash,
                            FileName = string.Empty,
                            ModFolderName = modFolderName,
                            Description = "Files have been uploaded for you.",
                            ModInfo = singleModInfo
                        };

                        await _hubContext.Clients.User(uid)
                            .SendAsync(nameof(ISpheneHub.Client_UserReceiveFileNotification), notification)
                            .ConfigureAwait(false);

                        pendingTransfers.Add(new PendingFileTransfer
                        {
                            RecipientUID = uid,
                            SenderUID = SpheneUser,
                            SenderAlias = senderAlias,
                            Hash = singleHash,
                            ModFolderName = modFolderName,
                            ModInfo = singleModInfo
                        });

                        await dbContext.ModShareHistory.AddAsync(new ModShareHistory
                        {
                            SenderUID = SpheneUser,
                            RecipientUID = uid,
                            Hash = singleHash,
                            SharedAt = DateTime.UtcNow
                        });
                    }
                }

                if (pendingTransfers.Any())
                {
                    try
                    {
                        _logger.LogInformation("{user}|FilesSend: notifying {uidCount} recipients for {hashCount} hashes", SpheneUser, recipientUids.Count, hashesForNotification.Count);
                        await dbContext.PendingFileTransfers.AddRangeAsync(pendingTransfers, HttpContext.RequestAborted).ConfigureAwait(false);
                        await dbContext.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "{user}|FilesSend: Failed to persist pending transfers/history", SpheneUser);
                    }
                }
            }
        }

        return Ok(JsonSerializer.Serialize(notCoveredFiles.Values.ToList()));
    }

    private static string NormalizeHashForLookup(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return string.Empty;
        }

        if (hash.Length > 128)
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[hash.Length];
        var length = 0;
        foreach (var c in hash)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                continue;
            }

            buffer[length++] = char.ToUpperInvariant(c);
            if (length > 40)
            {
                return string.Empty;
            }
        }

        if (length != 40)
        {
            return string.Empty;
        }

        return new string(buffer[..length]);
    }

    [HttpGet(SpheneFiles.ServerFiles_ModHistory)]
    public async Task<IActionResult> GetModUploadHistoryForCurrentUser()
    {
        using var dbContext = await _SpheneDbContext.CreateDbContextAsync();

        var uid = SpheneUser;

        var query = from file in dbContext.Files.AsNoTracking()
                    join mod in dbContext.ModFiles.AsNoTracking() on file.Hash equals mod.Hash
                    where file.UploaderUID == uid
                    orderby mod.UploadedDate descending
                    select new ModUploadHistoryEntryDto(
                        mod.Hash,
                        mod.Name,
                        mod.Author,
                        mod.Version,
                        mod.Description,
                        mod.Website,
                        mod.UploadedDate,
                        file.UploaderUID,
                        file.Size,
                        file.RawSize);

        var result = await query.ToListAsync().ConfigureAwait(false);

        return Ok(JsonSerializer.Serialize(result));
    }

    [HttpGet(SpheneFiles.ServerFiles_ModHistory_All)]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "Internal")]
    public async Task<IActionResult> GetModUploadHistoryForAll()
    {
        using var dbContext = await _SpheneDbContext.CreateDbContextAsync();

        var query = from file in dbContext.Files.AsNoTracking()
                    join mod in dbContext.ModFiles.AsNoTracking() on file.Hash equals mod.Hash
                    orderby mod.UploadedDate descending
                    select new ModUploadHistoryEntryDto(
                        mod.Hash,
                        mod.Name,
                        mod.Author,
                        mod.Version,
                        mod.Description,
                        mod.Website,
                        mod.UploadedDate,
                        file.UploaderUID,
                        file.Size,
                        file.RawSize);

        var result = await query.ToListAsync().ConfigureAwait(false);

        return Ok(JsonSerializer.Serialize(result));
    }

    [HttpGet(SpheneFiles.ServerFiles_ModDownloadHistory)]
    public async Task<IActionResult> GetModDownloadHistoryForCurrentUser()
    {
        using var dbContext = await _SpheneDbContext.CreateDbContextAsync().ConfigureAwait(false);

        var uid = SpheneUser;

        var query = from history in dbContext.ModDownloadHistory.AsNoTracking()
                    join file in dbContext.Files.AsNoTracking() on history.Hash equals file.Hash
                    join mod in dbContext.ModFiles.AsNoTracking() on history.Hash equals mod.Hash
                    where history.UserUID == uid
                    orderby history.DownloadedAt descending
                    select new ModDownloadHistoryEntryDto(
                        mod.Hash,
                        mod.Name,
                        mod.Author,
                        mod.Version,
                        mod.Description,
                        mod.Website,
                        history.DownloadedAt,
                        file.Size,
                        file.RawSize);

        var result = await query.ToListAsync().ConfigureAwait(false);

        return Ok(JsonSerializer.Serialize(result));
    }

    [HttpGet(SpheneFiles.ServerFiles_ModShareHistory)]
    public async Task<IActionResult> GetModShareHistoryForCurrentUser()
    {
        using var dbContext = await _SpheneDbContext.CreateDbContextAsync().ConfigureAwait(false);

        var uid = SpheneUser;

        var query = from history in dbContext.ModShareHistory.AsNoTracking()
                    join file in dbContext.Files.AsNoTracking() on history.Hash equals file.Hash
                    join mod in dbContext.ModFiles.AsNoTracking() on history.Hash equals mod.Hash
                    join user in dbContext.Users.AsNoTracking() on history.RecipientUID equals user.UID into users
                    from user in users.DefaultIfEmpty()
                    where history.SenderUID == uid
                    orderby history.SharedAt descending
                    select new ModShareHistoryEntryDto(
                        mod.Hash,
                        mod.Name,
                        mod.Author,
                        mod.Version,
                        mod.Description,
                        mod.Website,
                        history.SharedAt,
                        history.RecipientUID,
                        user != null ? user.Alias : null,
                        file.Size,
                        file.RawSize);

        var result = await query.ToListAsync().ConfigureAwait(false);

        return Ok(JsonSerializer.Serialize(result));
    }

    [HttpGet(SpheneFiles.ServerFiles_ModReceivedHistory)]
    public async Task<IActionResult> GetModReceivedHistoryForCurrentUser()
    {
        using var dbContext = await _SpheneDbContext.CreateDbContextAsync().ConfigureAwait(false);

        var uid = SpheneUser;

        var query = from history in dbContext.ModShareHistory.AsNoTracking()
                    join file in dbContext.Files.AsNoTracking() on history.Hash equals file.Hash
                    join mod in dbContext.ModFiles.AsNoTracking() on history.Hash equals mod.Hash
                    join user in dbContext.Users.AsNoTracking() on history.SenderUID equals user.UID into users
                    from user in users.DefaultIfEmpty()
                    where history.RecipientUID == uid
                    orderby history.SharedAt descending
                    select new ModReceivedHistoryEntryDto(
                        mod.Hash,
                        mod.Name,
                        mod.Author,
                        mod.Version,
                        mod.Description,
                        mod.Website,
                        history.SharedAt,
                        history.SenderUID,
                        user != null ? user.Alias : null,
                        file.Size,
                        file.RawSize);

        var result = await query.ToListAsync().ConfigureAwait(false);

        return Ok(JsonSerializer.Serialize(result));
    }

    [HttpPost(SpheneFiles.ServerFiles_PenumbraBackups + "/" + SpheneFiles.ServerFiles_PenumbraBackups_Create)]
    public async Task<IActionResult> CreatePenumbraModBackup([FromBody] PenumbraModBackupCreateDto dto)
    {
        if (dto == null)
        {
            return BadRequest();
        }

        var mods = dto.Mods ?? new List<PenumbraModBackupEntryDto>();
        if (mods.Count == 0)
        {
            return BadRequest();
        }

        const int maxMods = 5000;
        if (mods.Count > maxMods)
        {
            return BadRequest();
        }

        var normalizedMods = new List<PenumbraModBackupEntryDto>(mods.Count);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            if (mod == null)
            {
                continue;
            }

            var hash = (mod.Hash ?? string.Empty).Trim().ToUpperInvariant();
            if (hash.Length != 40 || !hash.All(char.IsAsciiLetterOrDigit))
            {
                continue;
            }

            hashes.Add(hash);

            var folder = (mod.ModFolderName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = hash;
            }

            var name = (mod.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = folder;
            }

            normalizedMods.Add(new PenumbraModBackupEntryDto(
                hash,
                folder,
                name,
                string.IsNullOrWhiteSpace(mod.Author) ? null : mod.Author,
                string.IsNullOrWhiteSpace(mod.Version) ? null : mod.Version,
                string.IsNullOrWhiteSpace(mod.Description) ? null : mod.Description,
                string.IsNullOrWhiteSpace(mod.Website) ? null : mod.Website));
        }

        if (normalizedMods.Count == 0)
        {
            return BadRequest();
        }

        using var dbContext = await _SpheneDbContext.CreateDbContextAsync().ConfigureAwait(false);

        var uploadedHashes = await dbContext.Files.AsNoTracking()
            .Where(f => hashes.Contains(f.Hash) && f.Uploaded)
            .Select(f => f.Hash)
            .ToListAsync()
            .ConfigureAwait(false);

        var uploaded = new HashSet<string>(uploadedHashes, StringComparer.OrdinalIgnoreCase);
        var missing = hashes.Where(h => !uploaded.Contains(h)).Select(h => h.ToUpperInvariant()).ToList();

        var backupName = (dto.BackupName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(backupName))
        {
            backupName = "Penumbra Backup";
        }

        var entity = new PenumbraModBackup
        {
            BackupId = Guid.NewGuid(),
            UserUID = SpheneUser,
            BackupName = backupName,
            CreatedAt = DateTime.UtcNow,
            IsComplete = missing.Count == 0,
            ModCount = normalizedMods.Count,
            Mods = normalizedMods
        };

        await dbContext.PenumbraModBackups.AddAsync(entity, HttpContext.RequestAborted).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        return Ok(JsonSerializer.Serialize(new PenumbraModBackupCreateResultDto(entity.BackupId, entity.IsComplete, missing)));
    }

    [HttpGet(SpheneFiles.ServerFiles_PenumbraBackups + "/" + SpheneFiles.ServerFiles_PenumbraBackups_List)]
    public async Task<IActionResult> ListPenumbraModBackups()
    {
        using var dbContext = await _SpheneDbContext.CreateDbContextAsync().ConfigureAwait(false);

        var uid = SpheneUser;

        var backups = await dbContext.PenumbraModBackups.AsNoTracking()
            .Where(b => b.UserUID == uid)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new PenumbraModBackupSummaryDto(
                b.BackupId,
                b.BackupName,
                b.CreatedAt,
                b.ModCount,
                b.IsComplete))
            .ToListAsync()
            .ConfigureAwait(false);

        return Ok(JsonSerializer.Serialize(backups));
    }

    [HttpGet(SpheneFiles.ServerFiles_PenumbraBackups + "/" + SpheneFiles.ServerFiles_PenumbraBackups_Get)]
    public async Task<IActionResult> GetPenumbraModBackup([FromQuery] Guid backupId)
    {
        if (backupId == Guid.Empty)
        {
            return BadRequest();
        }

        using var dbContext = await _SpheneDbContext.CreateDbContextAsync().ConfigureAwait(false);

        var uid = SpheneUser;

        var backup = await dbContext.PenumbraModBackups.AsNoTracking()
            .Where(b => b.UserUID == uid && b.BackupId == backupId)
            .Select(b => new PenumbraModBackupDto(
                b.BackupId,
                b.BackupName,
                b.CreatedAt,
                b.IsComplete,
                b.Mods))
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (backup == null)
        {
            return NotFound();
        }

        return Ok(JsonSerializer.Serialize(backup));
    }

    [HttpPost(SpheneFiles.ServerFiles_PenumbraBackups + "/" + SpheneFiles.ServerFiles_PenumbraBackups_Delete)]
    public async Task<IActionResult> DeletePenumbraModBackup([FromQuery] Guid backupId)
    {
        if (backupId == Guid.Empty)
        {
            return BadRequest();
        }

        using var dbContext = await _SpheneDbContext.CreateDbContextAsync().ConfigureAwait(false);

        var uid = SpheneUser;

        var entity = await dbContext.PenumbraModBackups
            .SingleOrDefaultAsync(b => b.UserUID == uid && b.BackupId == backupId, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (entity == null)
        {
            return NotFound();
        }

        dbContext.PenumbraModBackups.Remove(entity);
        await dbContext.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok();
    }

    private static async Task<List<string>> GetBidirectionalIndividualRecipientsAsync(SpheneDbContext dbContext, string senderUid, IReadOnlyCollection<string> recipientUids)
    {
        if (recipientUids.Count == 0)
        {
            return new List<string>();
        }

        var recipients = new HashSet<string>(recipientUids, StringComparer.Ordinal);

        var pairs = await dbContext.ClientPairs.AsNoTracking()
            .Where(cp => (cp.UserUID == senderUid && recipients.Contains(cp.OtherUserUID)) ||
                         (cp.OtherUserUID == senderUid && recipients.Contains(cp.UserUID)))
            .ToListAsync()
            .ConfigureAwait(false);

        List<string> validRecipients = new();
        foreach (var uid in recipients)
        {
            bool hasForward = pairs.Any(p => p.UserUID == senderUid && p.OtherUserUID == uid);
            bool hasReverse = pairs.Any(p => p.UserUID == uid && p.OtherUserUID == senderUid);
            if (hasForward && hasReverse)
            {
                validRecipients.Add(uid);
            }
        }

        return validRecipients;
    }

    [HttpPost(SpheneFiles.ServerFiles_Upload + "/{hash}")]
    [RequestSizeLimit(MaxUploadRequestSize)]
    public async Task<IActionResult> UploadFile(string hash, CancellationToken requestAborted)
    {
        using var dbContext = await _SpheneDbContext.CreateDbContextAsync();

        _logger.LogInformation("{user}|{file}: Uploading", SpheneUser, hash);

        hash = hash.ToUpperInvariant();
        var existingFile = await dbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
        if (existingFile != null) return Ok();

        SemaphoreSlim fileLock = await CreateFileLock(hash, requestAborted).ConfigureAwait(false);

        try
        {
            var existingFileCheck2 = await dbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
            if (existingFileCheck2 != null)
            {
                return Ok();
            }

            // copy the request body to memory
            using var memoryStream = new MemoryStream();
            await Request.Body.CopyToAsync(memoryStream, requestAborted).ConfigureAwait(false);

            _logger.LogDebug("{user}|{file}: Finished uploading", SpheneUser, hash);

            await StoreData(hash, dbContext, memoryStream).ConfigureAwait(false);

            return Ok();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "{user}|{file}: Error during file upload", SpheneUser, hash);
            return BadRequest();
        }
        finally
        {
            try
            {
                fileLock.Release();
                fileLock.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // it's disposed whatever
            }
            finally
            {
                _fileUploadLocks.TryRemove(hash, out _);
            }
        }
    }

    [HttpPost(SpheneFiles.ServerFiles_UploadMunged + "/{hash}")]
    [RequestSizeLimit(MaxUploadRequestSize)]
    public async Task<IActionResult> UploadFileMunged(string hash, CancellationToken requestAborted)
    {
        using var dbContext = await _SpheneDbContext.CreateDbContextAsync();

        _logger.LogInformation("{user}|{file}: Uploading munged", SpheneUser, hash);
        hash = hash.ToUpperInvariant();
        var existingFile = await dbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
        if (existingFile != null) return Ok();

        SemaphoreSlim fileLock = await CreateFileLock(hash, requestAborted).ConfigureAwait(false);

        try
        {
            var existingFileCheck2 = await dbContext.Files.SingleOrDefaultAsync(f => f.Hash == hash);
            if (existingFileCheck2 != null)
            {
                return Ok();
            }

            // copy the request body to memory
            using var compressedMungedStream = new MemoryStream();
            await Request.Body.CopyToAsync(compressedMungedStream, requestAborted).ConfigureAwait(false);
            var unmungedFile = compressedMungedStream.ToArray();
            MungeBuffer(unmungedFile.AsSpan());
            await using MemoryStream unmungedMs = new(unmungedFile);

            _logger.LogDebug("{user}|{file}: Finished uploading, unmunged stream", SpheneUser, hash);

            await StoreData(hash, dbContext, unmungedMs);

            return Ok();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "{user}|{file}: Error during file upload", SpheneUser, hash);
            return BadRequest();
        }
        finally
        {
            try
            {
                fileLock.Release();
                fileLock.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // it's disposed whatever
            }
            finally
            {
                _fileUploadLocks.TryRemove(hash, out _);
            }
        }
    }

    private async Task StoreData(string hash, SpheneDbContext dbContext, MemoryStream compressedFileStream)
    {
        var decompressedData = LZ4Wrapper.Unwrap(compressedFileStream.ToArray());
        // reset streams
        compressedFileStream.Seek(0, SeekOrigin.Begin);

        // compute hash to verify
        var hashString = BitConverter.ToString(SHA1.HashData(decompressedData))
            .Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        if (!string.Equals(hashString, hash, StringComparison.Ordinal))
            throw new InvalidOperationException($"{SpheneUser}|{hash}: Hash does not match file, computed: {hashString}, expected: {hash}");

        // save file
        var path = FilePathUtil.GetFilePath(_basePath, hash);
        using var fileStream = new FileStream(path, FileMode.Create);
        await compressedFileStream.CopyToAsync(fileStream).ConfigureAwait(false);
        _logger.LogDebug("{user}|{file}: Uploaded file saved to {path}", SpheneUser, hash, path);

        // update on db
        await dbContext.Files.AddAsync(new FileCache()
        {
            Hash = hash,
            UploadDate = DateTime.UtcNow,
            UploaderUID = SpheneUser,
            Size = compressedFileStream.Length,
            Uploaded = true,
            RawSize = decompressedData.LongLength
        }).ConfigureAwait(false);

        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogDebug("{user}|{file}: Uploaded file saved to DB", SpheneUser, hash);

        bool isColdStorage = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false);

        _metricsClient.IncGauge(isColdStorage ? MetricsAPI.GaugeFilesTotalColdStorage : MetricsAPI.GaugeFilesTotal, 1);
        _metricsClient.IncGauge(isColdStorage ? MetricsAPI.GaugeFilesTotalSizeColdStorage : MetricsAPI.GaugeFilesTotalSize, compressedFileStream.Length);
    }


    private async Task<SemaphoreSlim> CreateFileLock(string hash, CancellationToken requestAborted)
    {
        SemaphoreSlim? fileLock = null;
        bool successfullyWaited = false;
        while (!successfullyWaited && !requestAborted.IsCancellationRequested)
        {
            lock (_fileUploadLocks)
            {
                if (!_fileUploadLocks.TryGetValue(hash, out fileLock))
                {
                    _logger.LogDebug("{user}|{file}: Creating filelock", SpheneUser, hash);
                    _fileUploadLocks[hash] = fileLock = new SemaphoreSlim(1);
                }
            }

            try
            {
                _logger.LogDebug("{user}|{file}: Waiting for filelock", SpheneUser, hash);
                await fileLock.WaitAsync(requestAborted).ConfigureAwait(false);
                successfullyWaited = true;
            }
            catch (ObjectDisposedException)
            {
                _logger.LogWarning("{user}|{file}: Semaphore disposed, recreating", SpheneUser, hash);
            }
        }

        return fileLock;
    }

    private static void MungeBuffer(Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length; ++i)
        {
            buffer[i] ^= 42;
        }
    }
}

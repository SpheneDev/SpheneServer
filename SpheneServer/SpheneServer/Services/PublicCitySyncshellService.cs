using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sphene.SpheneServer.Data;
using SpheneShared.Data;
using SpheneShared.Models;
using SpheneShared.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Sphene.SpheneServer.Services;

public class PublicCitySyncshellService : IHostedService
{
    private readonly ILogger<PublicCitySyncshellService> _logger;
    private readonly IServiceProvider _serviceProvider;

    // Define the main cities with their territory and map IDs
    private readonly List<CityInfo> _mainCities = new()
    {
        new CityInfo("Limsa Lominsa", 129, 12),
        new CityInfo("New Gridania", 132, 2),
        new CityInfo("Ul'dah", 130, 13)
    };

    public PublicCitySyncshellService(ILogger<PublicCitySyncshellService> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting PublicCitySyncshellService...");

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SpheneDbContext>();

        try
        {
            await EnsurePublicCitySyncshellsExist(dbContext, cancellationToken);
            _logger.LogInformation("PublicCitySyncshellService started successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize public city syncshells");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping PublicCitySyncshellService...");
        return Task.CompletedTask;
    }

    private async Task EnsurePublicCitySyncshellsExist(SpheneDbContext dbContext, CancellationToken cancellationToken)
    {
        // Get the current server's identifier (we'll use a system user for ownership)
        var systemUser = await GetOrCreateSystemUser(dbContext);

        foreach (var city in _mainCities)
        {
            await EnsureCitySyncshellExists(dbContext, city, systemUser, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<User> GetOrCreateSystemUser(SpheneDbContext dbContext)
    {
        const string systemUserUID = "SYS_PUBSN";
        
        var systemUser = await dbContext.Users.FirstOrDefaultAsync(u => u.UID == systemUserUID);
        if (systemUser == null)
        {
            systemUser = new User
            {
                UID = systemUserUID,
                Alias = "System",
                LastLoggedIn = DateTime.UtcNow,
                IsAdmin = true
            };
            await dbContext.Users.AddAsync(systemUser);
            
            // Create default preferences for system user
            var defaultPrefs = new UserDefaultPreferredPermission
            {
                UserUID = systemUserUID,
                DisableGroupAnimations = false,
                DisableGroupSounds = false,
                DisableGroupVFX = false
            };
            await dbContext.UserDefaultPreferredPermissions.AddAsync(defaultPrefs);
        }

        return systemUser;
    }

    private async Task EnsureCitySyncshellExists(SpheneDbContext dbContext, CityInfo city, User systemUser, CancellationToken cancellationToken)
    {
        // Create a short alias for this city syncshell (max 10 chars for varchar constraint)
        var alias = city.Name switch
        {
            "Limsa Lominsa" => "Limsa",
            "New Gridania" => "Gridania", 
            "Ul'dah" => "Uldah",
            _ => city.Name.Substring(0, Math.Min(10, city.Name.Length))
        };
        
        // Check if a public syncshell for this city already exists
        var existingGroup = await dbContext.Groups
            .FirstOrDefaultAsync(g => g.Alias == alias && g.OwnerUID == systemUser.UID);

        if (existingGroup != null)
        {
            _logger.LogDebug("Public syncshell for {CityName} already exists with GID {GID}", city.Name, existingGroup.GID);
            
            // Update the welcome page image and text if it exists
            var existingWelcomePage = await dbContext.SyncshellWelcomePages
                .FirstOrDefaultAsync(wp => wp.GroupGID == existingGroup.GID);
                
            if (existingWelcomePage != null)
            {
                var newImageData = CityBannerImages.GetCityImageBytes(city.Name);
                if (newImageData != null)
                {
                    existingWelcomePage.WelcomeImageBase64 = Convert.ToBase64String(newImageData);
                    existingWelcomePage.ImageFileName = $"{city.Name.Replace(" ", "")}_welcome.png";
                    existingWelcomePage.ImageContentType = "image/png";
                    existingWelcomePage.ImageSize = newImageData.Length;
                    existingWelcomePage.UpdatedAt = DateTime.UtcNow;
                    
                    _logger.LogDebug("Updated welcome page image for {CityName} syncshell", city.Name);
                }
                
                // Update welcome text to latest version
                var newWelcomeText = GetDefaultWelcomeMessage(city.Name);
                if (existingWelcomePage.WelcomeText != newWelcomeText)
                {
                    existingWelcomePage.WelcomeText = newWelcomeText;
                    existingWelcomePage.UpdatedAt = DateTime.UtcNow;
                    
                    _logger.LogDebug("Updated welcome page text for {CityName} syncshell", city.Name);
                }
            }
            
            return;
        }

        // Generate unique GID
        var gid = await GenerateUniqueGID(dbContext);
        var password = StringUtils.GenerateRandomString(16);
        var hashedPassword = StringUtils.Sha256String(password);

        // Create the group
        var newGroup = new Group
        {
            GID = gid,
            HashedPassword = hashedPassword,
            InvitesEnabled = false, // Public syncshells don't need invites
            OwnerUID = systemUser.UID,
            Alias = alias,
            PreferDisableAnimations = false,
            PreferDisableSounds = false,
            PreferDisableVFX = false
        };

        await dbContext.Groups.AddAsync(newGroup);

        // Create area-bound syncshell
        var areaBoundSyncshell = new AreaBoundSyncshell
        {
            GroupGID = gid,
            Group = newGroup,
            AutoBroadcastEnabled = true,
            MaxAutoJoinUsers = 100, // Allow up to 100 users to auto-join
            JoinRules = GetDefaultCityRules(city.Name),
            RequireRulesAcceptance = true
        };

        await dbContext.AreaBoundSyncshells.AddAsync(areaBoundSyncshell);

        // Add the location binding
        var locationBinding = new AreaBoundLocation
        {
            GroupGID = gid,
            AreaBoundSyncshell = areaBoundSyncshell,
            TerritoryId = (uint)city.TerritoryId,
            MapId = (uint)city.MapId,
            ServerId = 0, // 0 means all servers
            DivisionId = 0,
            WardId = 0,
            HouseId = 0,
            RoomId = 0,
            MatchingMode = Sphene.API.Dto.Group.AreaMatchingMode.ExactMatch,
            CreatedAt = DateTime.UtcNow
        };

        await dbContext.AreaBoundLocations.AddAsync(locationBinding);

        // Create welcome page
        var welcomePage = new SyncshellWelcomePage
        {
            GroupGID = gid,
            Group = newGroup,
            WelcomeText = GetDefaultWelcomeMessage(city.Name),
            IsEnabled = true,
            ShowOnJoin = true,
            ShowOnAreaBoundJoin = true,
            WelcomeImageBase64 = Convert.ToBase64String(CityBannerImages.GetCityImageBytes(city.Name) ?? Array.Empty<byte>()),
            ImageFileName = $"{city.Name.Replace(" ", "")}_welcome.png",
            ImageContentType = "image/png",
            ImageSize = CityBannerImages.GetCityImageBytes(city.Name)?.Length ?? 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await dbContext.SyncshellWelcomePages.AddAsync(welcomePage);

        _logger.LogInformation("Created public syncshell for {CityName} with GID {GID}", city.Name, gid);
    }

    private async Task<string> GenerateUniqueGID(SpheneDbContext dbContext)
    {
        string gid;
        do
        {
            var randomPart = StringUtils.GenerateRandomString(6);
            gid = "PUB" + randomPart; // Use PUB prefix for public syncshells (3+6=9 chars, within 10 limit)
        }
        while (await dbContext.Groups.AnyAsync(g => g.GID == gid));

        return gid;
    }

    private string GetDefaultCityRules(string cityName)
    {
        return $@"Welcome to the public {cityName} syncshell!

Rules:
1. Be respectful to all members
2. No spam or excessive messaging
3. No advertising of Sphene in Public Chat
4. Follow the Final Fantasy XIV Terms of Service
5. Have fun and enjoy your time in {cityName}!

By accepting these rules, you agree to follow them while in this syncshell.";
    }

    private string GetDefaultWelcomeMessage(string cityName)
    {
        return $@"# Welcome to the <color=#00A3FF>{cityName} </color> Public Syncshell!


## This is the <color=#FFAE00>official Sphene community space</color> for <color=#FFAE00>{cityName}</color>. We're glad to have you here!

# <color=#00A3FF>Community Guidelines</color>

### -  Be respectful to all community members
### -  Follow the rules and community standards
### -  Keep conversations friendly and constructive
### -  Report any issues to moderators if needed

---

# <color=#00A3FF>About This Space</color>

### -  Open to all Sphene users visiting {cityName}
### -  Automatically managed by the Sphene system
### -  Designed for your comfort and enjoyment

---

We hope you feel welcome and comfortable here. Thank you for being part of our community!

#                                               With warm regards,
#                                                          <color=#00A3FF>The Sphene Team <3</color>";
    }

    private record CityInfo(string Name, int TerritoryId, int MapId);
}
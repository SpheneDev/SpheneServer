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

    // FFXIV World IDs - comprehensive list of all servers
    private readonly List<WorldInfo> _ffxivWorlds = new()
    {
        // North American - Aether Data Center
        new WorldInfo(73, "Adamantoise"),
        new WorldInfo(79, "Cactuar"),
        new WorldInfo(54, "Faerie"),
        new WorldInfo(63, "Gilgamesh"),
        new WorldInfo(40, "Jenova"),
        new WorldInfo(65, "Midgardsormr"),
        new WorldInfo(99, "Sargatanas"),
        new WorldInfo(57, "Siren"),
        
        // North American - Crystal Data Center
        new WorldInfo(91, "Balmung"),
        new WorldInfo(34, "Brynhildr"),
        new WorldInfo(74, "Coeurl"),
        new WorldInfo(62, "Diabolos"),
        new WorldInfo(81, "Goblin"),
        new WorldInfo(75, "Malboro"),
        new WorldInfo(37, "Mateus"),
        new WorldInfo(41, "Zalera"),
        
        // North American - Primal Data Center
        new WorldInfo(78, "Behemoth"),
        new WorldInfo(93, "Excalibur"),
        new WorldInfo(53, "Exodus"),
        new WorldInfo(35, "Famfrit"),
        new WorldInfo(95, "Hyperion"),
        new WorldInfo(55, "Lamia"),
        new WorldInfo(64, "Leviathan"),
        new WorldInfo(77, "Ultros"),
        
        // European - Chaos Data Center
        new WorldInfo(80, "Cerberus"),
        new WorldInfo(83, "Louisoix"),
        new WorldInfo(71, "Moogle"),
        new WorldInfo(39, "Omega"),
        new WorldInfo(85, "Phantom"),
        new WorldInfo(97, "Ragnarok"),
        new WorldInfo(400, "Sagittarius"),
        new WorldInfo(36, "Spriggan"),
        
        // European - Light Data Center
        new WorldInfo(66, "Alpha"),
        new WorldInfo(56, "Lich"),
        new WorldInfo(59, "Odin"),
        new WorldInfo(403, "Raiden"),
        new WorldInfo(67, "Shiva"),
        new WorldInfo(33, "Twintania"),
        new WorldInfo(42, "Zodiark"),
        
        // Japanese - Elemental Data Center
        new WorldInfo(90, "Aegis"),
        new WorldInfo(68, "Atomos"),
        new WorldInfo(45, "Carbuncle"),
        new WorldInfo(58, "Garuda"),
        new WorldInfo(94, "Gungnir"),
        new WorldInfo(49, "Kujata"),
        new WorldInfo(96, "Ramuh"),
        new WorldInfo(76, "Tonberry"),
        new WorldInfo(61, "Typhon"),
        new WorldInfo(50, "Unicorn"),
        
        // Japanese - Gaia Data Center
        new WorldInfo(43, "Alexander"),
        new WorldInfo(46, "Bahamut"),
        new WorldInfo(69, "Durandal"),
        new WorldInfo(92, "Fenrir"),
        new WorldInfo(98, "Ifrit"),
        new WorldInfo(72, "Ridill"),
        new WorldInfo(51, "Tiamat"),
        new WorldInfo(47, "Ultima"),
        new WorldInfo(48, "Valefor"),
        new WorldInfo(44, "Yojimbo"),
        
        // Japanese - Mana Data Center
        new WorldInfo(23, "Anima"),
        new WorldInfo(70, "Asura"),
        new WorldInfo(52, "Chocobo"),
        new WorldInfo(60, "Hades"),
        new WorldInfo(38, "Ixion"),
        new WorldInfo(86, "Masamune"),
        new WorldInfo(87, "Pandaemonium"),
        new WorldInfo(88, "Shinryu"),
        new WorldInfo(28, "Titan"),
        
        // Japanese - Meteor Data Center
        new WorldInfo(24, "Belias"),
        new WorldInfo(29, "Mandragora"),
        new WorldInfo(30, "Zeromus"),
        
        // Oceanian - Materia Data Center
        new WorldInfo(21, "Bismarck"),
        new WorldInfo(22, "Ravana"),
        new WorldInfo(86, "Sephirot"),
        new WorldInfo(87, "Sophia"),
        new WorldInfo(88, "Zurvan")
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

        // Create syncshells for each city on each world
        foreach (var world in _ffxivWorlds)
        {
            foreach (var city in _mainCities)
            {
                await EnsureCitySyncshellExists(dbContext, city, world, systemUser, cancellationToken);
            }
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

    private async Task EnsureCitySyncshellExists(SpheneDbContext dbContext, CityInfo city, WorldInfo world, User systemUser, CancellationToken cancellationToken)
    {
        // Create a short alias for this city syncshell (max 10 chars for varchar constraint)
        // Include world name to make it unique per server
        var baseAlias = city.Name switch
        {
            "Limsa Lominsa" => "Limsa",
            "New Gridania" => "Gridania", 
            "Ul'dah" => "Uldah",
            _ => city.Name.Substring(0, Math.Min(6, city.Name.Length))
        };
        
        // Truncate world name if needed to fit in 10 char limit
        var worldSuffix = world.Name.Length > 4 ? world.Name.Substring(0, 4) : world.Name;
        var alias = $"{baseAlias}_{worldSuffix}";
        if (alias.Length > 10)
        {
            alias = alias.Substring(0, 10);
        }
        
        // Check if a public syncshell for this city and world already exists
        var existingGroup = await dbContext.Groups
            .FirstOrDefaultAsync(g => g.Alias == alias && g.OwnerUID == systemUser.UID);

        if (existingGroup != null)
        {
            _logger.LogDebug("Public syncshell for {CityName} on {WorldName} already exists with GID {GID}", city.Name, world.Name, existingGroup.GID);
            
            // Load the AreaBoundSyncshell and its locations separately
            var existingAreaBoundSyncshell = await dbContext.AreaBoundSyncshells
                .Include(abs => abs.Locations)
                .FirstOrDefaultAsync(abs => abs.GroupGID == existingGroup.GID);
            
            // Ensure the location binding has the correct ServerId
            if (existingAreaBoundSyncshell?.Locations?.Any() == true)
            {
                var existingLocationBinding = existingAreaBoundSyncshell.Locations.First();
                if (existingLocationBinding.ServerId != world.Id)
                {
                    existingLocationBinding.ServerId = (ushort)world.Id;
                    _logger.LogDebug("Updated ServerId for {CityName} on {WorldName} syncshell to {ServerId}", city.Name, world.Name, world.Id);
                }
            }
            
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
                    
                    _logger.LogDebug("Updated welcome page image for {CityName} on {WorldName} syncshell", city.Name, world.Name);
                }
                
                // Update welcome text to latest version
                var newWelcomeText = GetDefaultWelcomeMessage(city.Name, world.Name);
                if (existingWelcomePage.WelcomeText != newWelcomeText)
                {
                    existingWelcomePage.WelcomeText = newWelcomeText;
                    existingWelcomePage.UpdatedAt = DateTime.UtcNow;
                    
                    _logger.LogDebug("Updated welcome page text for {CityName} on {WorldName} syncshell", city.Name, world.Name);
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
            JoinRules = GetDefaultCityRules(city.Name, world.Name),
            RequireRulesAcceptance = true
        };

        await dbContext.AreaBoundSyncshells.AddAsync(areaBoundSyncshell);

        // Add the location binding with the specific world ID
        var newLocationBinding = new AreaBoundLocation
        {
            GroupGID = gid,
            AreaBoundSyncshell = areaBoundSyncshell,
            TerritoryId = (uint)city.TerritoryId,
            MapId = (uint)city.MapId,
            ServerId = (ushort)world.Id, // Use the specific FFXIV world ID
            DivisionId = 0,
            WardId = 0,
            HouseId = 0,
            RoomId = 0,
            MatchingMode = Sphene.API.Dto.Group.AreaMatchingMode.ExactMatch,
            CreatedAt = DateTime.UtcNow
        };

        await dbContext.AreaBoundLocations.AddAsync(newLocationBinding);

        // Create welcome page
        var welcomePage = new SyncshellWelcomePage
        {
            GroupGID = gid,
            Group = newGroup,
            WelcomeText = GetDefaultWelcomeMessage(city.Name, world.Name),
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

        _logger.LogInformation("Created public syncshell for {CityName} on {WorldName} (ID: {WorldId}) with GID {GID}", city.Name, world.Name, world.Id, gid);
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

    private string GetDefaultCityRules(string cityName, string worldName)
    {
        return $@"Welcome to the public {cityName} syncshell for {worldName}!

Rules:
1. Be respectful to all members
2. No spam or excessive messaging
3. No advertising of Sphene in Public Chat
4. Follow the Final Fantasy XIV Terms of Service
5. Have fun and enjoy your time in {cityName}!

By accepting these rules, you agree to follow them while in this syncshell.";
    }

    private string GetDefaultWelcomeMessage(string cityName, string worldName)
    {
        return $@"# Welcome to the <color=#00A3FF>{cityName} </color> Public Syncshell for <color=#FFAE00>{worldName}</color>!


## This is the <color=#FFAE00>official Sphene community space</color> for <color=#FFAE00>{cityName}</color> on <color=#FFAE00>{worldName}</color>. We're glad to have you here!

# <color=#00A3FF>Community Guidelines</color>

### -  Be respectful to all community members
### -  Follow the rules and community standards
### -  Keep conversations friendly and constructive
### -  Report any issues to moderators if needed

---

# <color=#00A3FF>About This Space</color>

### -  Open to all Sphene users visiting {cityName} on {worldName}
### -  Automatically managed by the Sphene system
### -  Designed for your comfort and enjoyment

---

We hope you feel welcome and comfortable here. Thank you for being part of our community!

#                                               With warm regards,
#                                                          <color=#00A3FF>The Sphene Team <3</color>";
    }

    private record CityInfo(string Name, int TerritoryId, int MapId);
    private record WorldInfo(int Id, string Name);
}
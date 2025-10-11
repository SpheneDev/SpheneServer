using Sphene.API.Dto.CharaData;
using Sphene.API.Dto.User;
using SpheneServer.Utils;
using SpheneShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace SpheneServer.Hubs;

public partial class SpheneHub
{
    [Authorize(Policy = "Identified")]
    public async Task<List<UserHousingPropertyDto>> UserGetHousingProperties()
    {
        _logger.LogCallInfo();

        var properties = await DbContext.UserHousingProperties
            .Where(p => p.UserUID == UserUID)
            .ToListAsync()
            .ConfigureAwait(false);

        var result = properties.Select(p => new UserHousingPropertyDto
        {
            Id = p.Id,
            Location = new LocationInfo
            {
                ServerId = p.ServerId,
                MapId = p.MapId,
                TerritoryId = p.TerritoryId,
                DivisionId = p.DivisionId,
                WardId = p.WardId,
                HouseId = p.HouseId,
                RoomId = p.RoomId,
                IsIndoor = p.IsIndoor
            },
            AllowOutdoor = p.AllowOutdoor,
            AllowIndoor = p.AllowIndoor,
            PreferOutdoorSyncshells = p.PreferOutdoorSyncshells,
            PreferIndoorSyncshells = p.PreferIndoorSyncshells,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt
        }).ToList();

        _logger.LogCallInfo(SpheneHubLogger.Args($"Returning {result.Count} housing properties"));
        return result;
    }

    [Authorize(Policy = "Identified")]
    public async Task<UserHousingPropertyDto?> UserSetHousingProperty(UserHousingPropertyUpdateDto dto)
    {
        // Add explicit entry logging to verify method is called
        Console.WriteLine($"[SERVER DEBUG] UserSetHousingProperty called with: {dto.Location}");
        _logger.LogCallInfo(SpheneHubLogger.Args("METHOD_ENTRY", dto.Location.ToString()));

        try
        {
            // Validate input data
            if (dto.Location.ServerId == 0)
            {
                Console.WriteLine("[SERVER DEBUG] UserSetHousingProperty: Invalid input - dto or Location is null");
                _logger.LogCallWarning(SpheneHubLogger.Args("Invalid input - dto or Location is null"));
                return null;
            }

            _logger.LogDebug($"UserSetHousingProperty: Processing location - ServerId: {dto.Location.ServerId}, TerritoryId: {dto.Location.TerritoryId}, WardId: {dto.Location.WardId}, HouseId: {dto.Location.HouseId}");

            // Find existing property for this location with more detailed logging
            _logger.LogDebug($"UserSetHousingProperty: Searching for existing property - UserUID: {UserUID}, ServerId: {dto.Location.ServerId}, MapId: {dto.Location.MapId}, TerritoryId: {dto.Location.TerritoryId}, DivisionId: {dto.Location.DivisionId}, WardId: {dto.Location.WardId}, HouseId: {dto.Location.HouseId}, RoomId: {dto.Location.RoomId}");
            
            var existingProperty = await DbContext.UserHousingProperties
                .FirstOrDefaultAsync(p => p.UserUID == UserUID &&
                                        p.ServerId == dto.Location.ServerId &&
                                        p.MapId == dto.Location.MapId &&
                                        p.TerritoryId == dto.Location.TerritoryId &&
                                        p.DivisionId == dto.Location.DivisionId &&
                                        p.WardId == dto.Location.WardId &&
                                        p.HouseId == dto.Location.HouseId &&
                                        p.RoomId == dto.Location.RoomId)
                .ConfigureAwait(false);
                
            _logger.LogDebug($"UserSetHousingProperty: Existing property found: {existingProperty != null} (ID: {existingProperty?.Id ?? 0})");

            if (existingProperty != null)
            {
                _logger.LogDebug($"UserSetHousingProperty: Updating existing property with ID {existingProperty.Id}");
                // Update existing property
                existingProperty.AllowOutdoor = dto.AllowOutdoor;
                existingProperty.AllowIndoor = dto.AllowIndoor;
                existingProperty.PreferOutdoorSyncshells = dto.PreferOutdoorSyncshells;
                existingProperty.PreferIndoorSyncshells = dto.PreferIndoorSyncshells;
                existingProperty.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _logger.LogDebug("UserSetHousingProperty: Creating new property");
                // Create new property
                existingProperty = new UserHousingProperty
                {
                    UserUID = UserUID,
                    ServerId = dto.Location.ServerId,
                    MapId = dto.Location.MapId,
                    TerritoryId = dto.Location.TerritoryId,
                    DivisionId = dto.Location.DivisionId,
                    WardId = dto.Location.WardId,
                    HouseId = dto.Location.HouseId,
                    RoomId = dto.Location.RoomId,
                    IsIndoor = dto.Location.IsIndoor,
                    AllowOutdoor = dto.AllowOutdoor,
                    AllowIndoor = dto.AllowIndoor,
                    PreferOutdoorSyncshells = dto.PreferOutdoorSyncshells,
                    PreferIndoorSyncshells = dto.PreferIndoorSyncshells,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                await DbContext.UserHousingProperties.AddAsync(existingProperty).ConfigureAwait(false);
            }

            await DbContext.SaveChangesAsync().ConfigureAwait(false);

            var result = new UserHousingPropertyDto
            {
                Id = existingProperty.Id,
                Location = dto.Location,
                AllowOutdoor = existingProperty.AllowOutdoor,
                AllowIndoor = existingProperty.AllowIndoor,
                PreferOutdoorSyncshells = existingProperty.PreferOutdoorSyncshells,
                PreferIndoorSyncshells = existingProperty.PreferIndoorSyncshells,
                CreatedAt = existingProperty.CreatedAt,
                UpdatedAt = existingProperty.UpdatedAt
            };

            _logger.LogCallInfo(SpheneHubLogger.Args("Success"));
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args($"Failed to set housing property for location {dto?.Location}: {ex.Message}"));
            return null;
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> UserDeleteHousingProperty(LocationInfo location)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(location));
        
        // Add debug logging to trace search parameters
        _logger.LogDebug($"UserDeleteHousingProperty: Searching for property - UserUID: {UserUID}, ServerId: {location.ServerId}, MapId: {location.MapId}, TerritoryId: {location.TerritoryId}, DivisionId: {location.DivisionId}, WardId: {location.WardId}, HouseId: {location.HouseId}, RoomId: {location.RoomId}");

        var property = await DbContext.UserHousingProperties
            .FirstOrDefaultAsync(p => p.UserUID == UserUID &&
                                    p.ServerId == location.ServerId &&
                                    p.MapId == location.MapId &&
                                    p.TerritoryId == location.TerritoryId &&
                                    p.DivisionId == location.DivisionId &&
                                    p.WardId == location.WardId &&
                                    p.HouseId == location.HouseId &&
                                    p.RoomId == location.RoomId)
            .ConfigureAwait(false);

        _logger.LogDebug($"UserDeleteHousingProperty: Property found: {property != null} (ID: {property?.Id ?? 0})");

        if (property == null)
        {
            _logger.LogCallInfo(SpheneHubLogger.Args("Property not found"));
            return false;
        }

        DbContext.UserHousingProperties.Remove(property);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args("Success"));
        return true;
    }
}
using Sphene.API.Routes;
using SpheneAuthService.Services;
using SpheneShared;
using SpheneShared.Data;
using SpheneShared.Services;
using SpheneShared.Utils;
using SpheneShared.Utils.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace SpheneAuthService.Controllers;

[Route(SpheneAuth.Auth)]
public class JwtController : AuthControllerBase
{
    public JwtController(ILogger<JwtController> logger,
        IHttpContextAccessor accessor, IDbContextFactory<SpheneDbContext> spheneDbContextFactory,
        SecretKeyAuthenticatorService secretKeyAuthenticatorService,
        IConfigurationService<AuthServiceConfiguration> configuration,
        IDatabase redisDb, GeoIPService geoIPProvider)
            : base(logger, accessor, spheneDbContextFactory, secretKeyAuthenticatorService,
                configuration, redisDb, geoIPProvider)
    {
    }

    [AllowAnonymous]
    [HttpPost(SpheneAuth.Auth_CreateIdent)]
    public async Task<IActionResult> CreateToken(string auth, string charaIdent)
    {
        using var dbContext = await SpheneDbContextFactory.CreateDbContextAsync();
        return await AuthenticateInternal(dbContext, auth, charaIdent).ConfigureAwait(false);
    }

    [Authorize(Policy = "Authenticated")]
    [HttpGet(SpheneAuth.Auth_RenewToken)]
    public async Task<IActionResult> RenewToken()
    {
        using var dbContext = await SpheneDbContextFactory.CreateDbContextAsync();
        try
        {
            var uid = HttpContext.User.Claims.Single(p => string.Equals(p.Type, SpheneClaimTypes.Uid, StringComparison.Ordinal))!.Value;
            var ident = HttpContext.User.Claims.Single(p => string.Equals(p.Type, SpheneClaimTypes.CharaIdent, StringComparison.Ordinal))!.Value;
            var alias = HttpContext.User.Claims.SingleOrDefault(p => string.Equals(p.Type, SpheneClaimTypes.Alias))?.Value ?? string.Empty;

            if (await dbContext.Auth.Where(u => u.UserUID == uid || u.PrimaryUserUID == uid).AnyAsync(a => a.MarkForBan))
            {
                var userAuth = await dbContext.Auth.SingleAsync(u => u.UserUID == uid);
                await EnsureBan(uid, userAuth.PrimaryUserUID, ident);

                return Unauthorized("Your Sphene account is banned.");
            }

            if (await IsIdentBanned(dbContext, ident))
            {
                return Unauthorized("Your XIV service account is banned from using the service.");
            }

            Logger.LogInformation("RenewToken:SUCCESS:{id}:{ident}", uid, ident);
            return await CreateJwtFromId(uid, ident, alias);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "RenewToken:FAILURE");
            return Unauthorized("Unknown error while renewing authentication token");
        }
    }

    protected async Task<IActionResult> AuthenticateInternal(SpheneDbContext dbContext, string auth, string charaIdent)
    {
        try
        {
            if (string.IsNullOrEmpty(auth)) return BadRequest("No Authkey");
            if (string.IsNullOrEmpty(charaIdent)) return BadRequest("No CharaIdent");

            var ip = HttpAccessor.GetIpAddress();

            var hashedAuth = StringUtils.Sha256String(auth);
            var authResult = await SecretKeyAuthenticatorService.AuthorizeAsync(ip, hashedAuth);

            if (!authResult.Success)
            {
                var autoCreate = Configuration.GetValueOrDefault(nameof(AuthServiceConfiguration.AutoCreateCharaHashOnSecretKeyLogin), false);
                if (autoCreate)
                {
                    var user = new SpheneShared.Models.User();
                    var hasValidUid = false;
                    while (!hasValidUid)
                    {
                        var uid = StringUtils.GenerateRandomString(10);
                        if (await dbContext.Users.AnyAsync(u => u.UID == uid || u.Alias == uid).ConfigureAwait(false))
                            continue;
                        user.UID = uid;
                        hasValidUid = true;
                    }

                    user.Alias = string.Empty;
                    user.LastLoggedIn = DateTime.UtcNow;

                    var authEntity = new SpheneShared.Models.Auth()
                    {
                        HashedKey = hashedAuth,
                        User = user,
                    };

                    await dbContext.Users.AddAsync(user).ConfigureAwait(false);
                    await dbContext.Auth.AddAsync(authEntity).ConfigureAwait(false);
                    await dbContext.SaveChangesAsync().ConfigureAwait(false);

                    Logger.LogInformation("Authenticate:AUTOCREATE:{uid}:{ident}", user.UID, charaIdent);

                    var created = new SpheneAuthService.Authentication.SecretKeyAuthReply(true, user.UID, user.UID, user.Alias, false, false, false);
                    return await GenericAuthResponse(dbContext, charaIdent, created);
                }
            }

            return await GenericAuthResponse(dbContext, charaIdent, authResult);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Authenticate:UNKNOWN");
            return Unauthorized("Unknown internal server error during authentication");
        }
    }
}

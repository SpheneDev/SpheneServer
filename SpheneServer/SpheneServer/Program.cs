using Microsoft.EntityFrameworkCore;
using SpheneShared.Data;
using SpheneShared.Metrics;
using SpheneShared.Services;
using SpheneShared.Utils.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpheneServer;

public class Program
{
    public static void Main(string[] args)
    {
        var hostBuilder = CreateHostBuilder(args);
        using var host = hostBuilder.Build();
        using (var scope = host.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var factory = services.GetRequiredService<IDbContextFactory<SpheneDbContext>>();
            using var context = factory.CreateDbContext();
            var options = services.GetRequiredService<IConfigurationService<ServerConfiguration>>();
            var logger = host.Services.GetRequiredService<ILogger<Program>>();

            if (options.IsMain)
            {
                context.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
                context.Database.Migrate();
                context.Database.SetCommandTimeout(TimeSpan.FromSeconds(30));
                context.SaveChanges();

                // clean up residuals
                var looseFiles = context.Files.Where(f => f.Uploaded == false);
                var unfinishedRegistrations = context.LodeStoneAuth.Where(c => c.StartedAt != null);
                context.RemoveRange(unfinishedRegistrations);
                context.RemoveRange(looseFiles);
                context.SaveChanges();

                MigrateProfileThemeRowFx(context, logger);

                logger.LogInformation(options.ToString());
            }
            var metrics = services.GetRequiredService<SpheneMetrics>();

            metrics.SetGaugeTo(MetricsAPI.GaugeUsersRegistered, context.Users.AsNoTracking().Count());
            metrics.SetGaugeTo(MetricsAPI.GaugePairs, context.ClientPairs.AsNoTracking().Count());
            metrics.SetGaugeTo(MetricsAPI.GaugePairsPaused, context.Permissions.AsNoTracking().Where(p=>p.IsPaused).Count());

        }

        if (args.Length == 0 || !string.Equals(args[0], "dry", StringComparison.Ordinal))
        {
            try
            {
                host.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }

    // Enum bit order of the client's ProfileRowFx - fx keys serialize as
    // comma-separated names in this order, legacy keys hold the first name.
    private static readonly string[] RowFxOrder =
        ["Glow", "Sheen", "Pulse", "Sweep", "Lift", "EdgeBar", "Tint", "Sparkle", "Wave"];

    /// <summary>
    /// One-time data migration: every stored profile theme must carry the
    /// Pulse+Glow list-row baseline. Themes without the bits get the flag
    /// keys rewritten in place; unparseable or non-object themes are left
    /// alone (clients fall back to defaults that already include them).
    /// </summary>
    private static void MigrateProfileThemeRowFx(SpheneDbContext context, ILogger logger)
    {
        var patched = 0;
        foreach (var profile in context.UserProfileData)
        {
            var theme = profile.ProfileTheme;
            JsonObject obj;
            if (string.IsNullOrWhiteSpace(theme))
            {
                obj = new JsonObject();
            }
            else
            {
                try
                {
                    if (JsonNode.Parse(theme) is not JsonObject parsed)
                    {
                        continue;
                    }
                    obj = parsed;
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            var changed = EnsureThemeFlags(obj, "rowHover", "rowHoverFx", "Glow")
                | EnsureThemeFlags(obj, "rowIdle", "rowIdleFx", null);
            if (!changed)
            {
                continue;
            }
            profile.ProfileTheme = obj.ToJsonString();
            patched++;
        }

        if (patched > 0)
        {
            context.SaveChanges();
            logger.LogInformation("Profile theme migration: applied the Pulse+Glow row baseline to {count} profiles", patched);
        }
    }

    /// <summary>Adds Glow and Pulse to one effect layer and rewrites the
    /// keys the client serialization uses; returns false when already set.</summary>
    private static bool EnsureThemeFlags(JsonObject obj, string legacyKey, string fxKey, string fallback)
    {
        var names = new List<string>();
        var raw = obj[fxKey]?.GetValue<string>() ?? obj[legacyKey]?.GetValue<string>() ?? fallback;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var part in raw.Split(','))
            {
                var name = part.Trim();
                if (name.Length > 0 && !name.Equals("None", StringComparison.OrdinalIgnoreCase)
                    && !names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                }
            }
        }

        var hadGlow = names.Contains("Glow", StringComparer.Ordinal);
        var hadPulse = names.Contains("Pulse", StringComparer.Ordinal);
        if (hadGlow && hadPulse)
        {
            return false;
        }
        if (!hadGlow) names.Add("Glow");
        if (!hadPulse) names.Add("Pulse");

        // emit in enum order; names a newer client invented sort last
        var ordered = names
            .OrderBy(n => Array.IndexOf(RowFxOrder, n) is var i && i >= 0 ? i : RowFxOrder.Length)
            .ToList();
        obj[legacyKey] = ordered[0];
        obj[fxKey] = ordered.Count > 1 ? string.Join(", ", ordered) : null;
        return true;
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddConsole();
        });
        var logger = loggerFactory.CreateLogger<Startup>();
        return Host.CreateDefaultBuilder(args)
            .UseSystemd()
            .UseConsoleLifetime()
            .ConfigureAppConfiguration((ctx, config) =>
            {
                var appSettingsPath = Environment.GetEnvironmentVariable("APPSETTINGS_PATH");
                if (!string.IsNullOrEmpty(appSettingsPath))
                {
                    config.AddJsonFile(appSettingsPath, optional: true, reloadOnChange: true);
                }
                else
                {
                    var basePath = AppContext.BaseDirectory;
                    config.SetBasePath(basePath);
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                    config.AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
                }

                config.AddEnvironmentVariables();
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseContentRoot(AppContext.BaseDirectory);
                webBuilder.ConfigureLogging((ctx, builder) =>
                {
                    builder.AddConfiguration(ctx.Configuration.GetSection("Logging"));
                    builder.AddFile(o => o.RootPath = AppContext.BaseDirectory);
                });
                webBuilder.UseStartup(ctx => new Startup(ctx.Configuration, logger));
            });
    }
}

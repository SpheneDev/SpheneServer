using SpheneAuthService.Controllers;
using SpheneShared.Metrics;
using SpheneShared.Services;
using SpheneShared.Utils;
using Microsoft.AspNetCore.Mvc.Controllers;
using StackExchange.Redis.Extensions.Core.Configuration;
using StackExchange.Redis.Extensions.System.Text.Json;
using StackExchange.Redis;
using System.Net;
using SpheneAuthService.Services;
using SpheneShared.RequirementHandlers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using SpheneShared.Data;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using SpheneShared.Utils.Configuration;
using StackExchange.Redis.Extensions.Core.Abstractions;
using Npgsql;

namespace SpheneAuthService;

public class Startup
{
    private readonly IConfiguration _configuration;
    private ILogger<Startup> _logger;

    public Startup(IConfiguration configuration, ILogger<Startup> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
    {
        var config = app.ApplicationServices.GetRequiredService<IConfigurationService<SpheneConfigurationBase>>();

        app.UseRouting();

        app.UseHttpMetrics();

        app.UseAuthentication();
        app.UseAuthorization();

        KestrelMetricServer metricServer = new KestrelMetricServer(config.GetValueOrDefault<int>(nameof(SpheneConfigurationBase.MetricsPort), 4985));
        metricServer.Start();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();

            foreach (var source in endpoints.DataSources.SelectMany(e => e.Endpoints).Cast<RouteEndpoint>())
            {
                if (source == null) continue;
                _logger.LogInformation("Endpoint: {url} ", source.RoutePattern.RawText);
            }
        });
    }

    public void ConfigureServices(IServiceCollection services)
    {
        var spheneConfig = _configuration.GetRequiredSection("Sphene");

        services.AddHttpContextAccessor();

        ConfigureRedis(services, spheneConfig);

        services.AddSingleton<SecretKeyAuthenticatorService>();
        services.AddSingleton<GeoIPService>();

        services.AddHostedService(provider => provider.GetRequiredService<GeoIPService>());

        services.Configure<AuthServiceConfiguration>(_configuration.GetRequiredSection("Sphene"));
        services.Configure<SpheneConfigurationBase>(_configuration.GetRequiredSection("Sphene"));

        services.AddSingleton<ServerTokenGenerator>();

        ConfigureAuthorization(services);

        ConfigureDatabase(services, spheneConfig);

        ConfigureConfigServices(services);

        ConfigureMetrics(services);

        services.AddHealthChecks();
        services.AddControllers().ConfigureApplicationPartManager(a =>
        {
            a.FeatureProviders.Remove(a.FeatureProviders.OfType<ControllerFeatureProvider>().First());
            a.FeatureProviders.Add(new AllowedControllersFeatureProvider(typeof(JwtController), typeof(OAuthController)));
        });
    }

    private static void ConfigureAuthorization(IServiceCollection services)
    {
        services.AddTransient<IAuthorizationHandler, RedisDbUserRequirementHandler>();
        services.AddTransient<IAuthorizationHandler, ValidTokenRequirementHandler>();
        services.AddTransient<IAuthorizationHandler, ExistingUserRequirementHandler>();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfigurationService<SpheneConfigurationBase>>((options, config) =>
            {
                options.TokenValidationParameters = new()
                {
                    ValidateIssuer = false,
                    ValidateLifetime = true,
                    ValidateAudience = false,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(config.GetValue<string>(nameof(SpheneConfigurationBase.Jwt)))),
                };
            });

        services.AddAuthentication(o =>
        {
            o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            o.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        }).AddJwtBearer();

        services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().Build();
            options.AddPolicy("OAuthToken", policy =>
            {
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                policy.AddRequirements(new ValidTokenRequirement());
                policy.AddRequirements(new ExistingUserRequirement());
                policy.RequireClaim(SpheneClaimTypes.OAuthLoginToken, "True");
            });
            options.AddPolicy("Authenticated", policy =>
            {
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ValidTokenRequirement());
            });
            options.AddPolicy("Identified", policy =>
            {
                policy.AddRequirements(new UserRequirement(UserRequirements.Identified));
                policy.AddRequirements(new ValidTokenRequirement());

            });
            options.AddPolicy("Admin", policy =>
            {
                policy.AddRequirements(new UserRequirement(UserRequirements.Identified | UserRequirements.Administrator));
                policy.AddRequirements(new ValidTokenRequirement());

            });
            options.AddPolicy("Moderator", policy =>
            {
                policy.AddRequirements(new UserRequirement(UserRequirements.Identified | UserRequirements.Moderator | UserRequirements.Administrator));
                policy.AddRequirements(new ValidTokenRequirement());
            });
            options.AddPolicy("Internal", new AuthorizationPolicyBuilder().RequireClaim(SpheneClaimTypes.Internal, "true").Build());
        });
    }

    private static void ConfigureMetrics(IServiceCollection services)
    {
        services.AddSingleton<SpheneMetrics>(m => new SpheneMetrics(m.GetService<ILogger<SpheneMetrics>>(), new List<string>
        {
            MetricsAPI.CounterAuthenticationCacheHits,
            MetricsAPI.CounterAuthenticationFailures,
            MetricsAPI.CounterAuthenticationRequests,
            MetricsAPI.CounterAuthenticationSuccesses,
        }, new List<string>
        {
            MetricsAPI.GaugeAuthenticationCacheEntries,
        }));
    }

    private void ConfigureRedis(IServiceCollection services, IConfigurationSection spheneConfig)
    {
        // configure redis for SignalR
        var redisConnection = spheneConfig.GetValue(nameof(ServerConfiguration.RedisConnectionString), string.Empty);
        var options = ConfigurationOptions.Parse(redisConnection);

        var endpoint = options.EndPoints[0];
        string address = "";
        int port = 0;
        
        if (endpoint is DnsEndPoint dnsEndPoint) { address = dnsEndPoint.Host; port = dnsEndPoint.Port; }
        if (endpoint is IPEndPoint ipEndPoint) { address = ipEndPoint.Address.ToString(); port = ipEndPoint.Port; }

        var muxer = ConnectionMultiplexer.Connect(options);
        var db = muxer.GetDatabase();
        services.AddSingleton<IDatabase>(db);

        _logger.LogInformation("Setting up Redis to connect to {host}:{port}", address, port);
    }
    private void ConfigureConfigServices(IServiceCollection services)
    {
        services.AddSingleton<IConfigurationService<AuthServiceConfiguration>, SpheneConfigurationServiceServer<AuthServiceConfiguration>>();
        services.AddSingleton<IConfigurationService<SpheneConfigurationBase>, SpheneConfigurationServiceServer<SpheneConfigurationBase>>();
    }

    private void ConfigureDatabase(IServiceCollection services, IConfigurationSection spheneConfig)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();
        services.AddSingleton(dataSource);

        services.AddDbContextPool<SpheneDbContext>(options =>
        {
            options.UseNpgsql(dataSource, builder =>
            {
                builder.MigrationsHistoryTable("_efmigrationshistory", "public");
                builder.MigrationsAssembly("SpheneShared");
            }).UseSnakeCaseNamingConvention();
            options.EnableThreadSafetyChecks(false);
        }, spheneConfig.GetValue(nameof(SpheneConfigurationBase.DbContextPoolSize), 1024));
        services.AddDbContextFactory<SpheneDbContext>(options =>
        {
            options.UseNpgsql(dataSource, builder =>
            {
                builder.MigrationsHistoryTable("_efmigrationshistory", "public");
                builder.MigrationsAssembly("SpheneShared");
            }).UseSnakeCaseNamingConvention();
            options.EnableThreadSafetyChecks(false);
        });
    }
}

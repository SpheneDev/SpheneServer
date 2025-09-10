using SpheneServices.Discord;
using SpheneShared.Data;
using SpheneShared.Metrics;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using SpheneShared.Utils;
using SpheneShared.Services;
using StackExchange.Redis;
using SpheneShared.Utils.Configuration;

namespace SpheneServices;

public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        var config = app.ApplicationServices.GetRequiredService<IConfigurationService<SpheneConfigurationBase>>();

        var metricServer = new KestrelMetricServer(config.GetValueOrDefault<int>(nameof(SpheneConfigurationBase.MetricsPort), 4982));
        metricServer.Start();
    }

    public void ConfigureServices(IServiceCollection services)
    {
        var spheneConfig = Configuration.GetSection("Sphene");

        services.AddDbContextPool<SpheneDbContext>(options =>
        {
            options.UseNpgsql(Configuration.GetConnectionString("DefaultConnection"), builder =>
            {
                builder.MigrationsHistoryTable("_efmigrationshistory", "public");
            }).UseSnakeCaseNamingConvention();
            options.EnableThreadSafetyChecks(false);
        }, Configuration.GetValue(nameof(SpheneConfigurationBase.DbContextPoolSize), 1024));
        services.AddDbContextFactory<SpheneDbContext>(options =>
        {
            options.UseNpgsql(Configuration.GetConnectionString("DefaultConnection"), builder =>
            {
                builder.MigrationsHistoryTable("_efmigrationshistory", "public");
                builder.MigrationsAssembly("SpheneShared");
            }).UseSnakeCaseNamingConvention();
            options.EnableThreadSafetyChecks(false);
        });

        services.AddSingleton(m => new SpheneMetrics(m.GetService<ILogger<SpheneMetrics>>(), new List<string> { },
        new List<string> { }));

        var redis = spheneConfig.GetValue(nameof(ServerConfiguration.RedisConnectionString), string.Empty);
        var options = ConfigurationOptions.Parse(redis);
        options.ClientName = "Sphene";
        options.ChannelPrefix = "UserData";
        ConnectionMultiplexer connectionMultiplexer = ConnectionMultiplexer.Connect(options);
        services.AddSingleton<IConnectionMultiplexer>(connectionMultiplexer);

        services.Configure<ServicesConfiguration>(Configuration.GetRequiredSection("Sphene"));
        services.Configure<ServerConfiguration>(Configuration.GetRequiredSection("Sphene"));
        services.Configure<SpheneConfigurationBase>(Configuration.GetRequiredSection("Sphene"));
        services.AddSingleton(Configuration);
        services.AddSingleton<ServerTokenGenerator>();
        services.AddSingleton<DiscordBotServices>();
        services.AddHostedService<DiscordBot>();
        services.AddSingleton<IConfigurationService<ServicesConfiguration>, SpheneConfigurationServiceServer<ServicesConfiguration>>();
        services.AddSingleton<IConfigurationService<ServerConfiguration>, SpheneConfigurationServiceClient<ServerConfiguration>>();
        services.AddSingleton<IConfigurationService<SpheneConfigurationBase>, SpheneConfigurationServiceClient<SpheneConfigurationBase>>();
        services.AddHostedService(p => (SpheneConfigurationServiceClient<SpheneConfigurationBase>)p.GetService<IConfigurationService<SpheneConfigurationBase>>());
        services.AddHostedService(p => (SpheneConfigurationServiceClient<ServerConfiguration>)p.GetService<IConfigurationService<ServerConfiguration>>());
    }
}

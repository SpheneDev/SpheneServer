using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using MareSynchronosShared.Data;
using MareSynchronosShared.Models;
using MareSynchronosShared.Services;
using MareSynchronosShared.Utils;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Text.RegularExpressions;

namespace MareSynchronosServices.Discord;

public partial class MareWizardModule : InteractionModuleBase
{
    private ILogger<MareModule> _logger;
    private DiscordBotServices _botServices;
    private IConfigurationService<ServerConfiguration> _mareClientConfigurationService;
    private IConfigurationService<ServicesConfiguration> _mareServicesConfiguration;
    private IConnectionMultiplexer _connectionMultiplexer;
    private readonly IDbContextFactory<MareDbContext> _dbContextFactory;
    private Random random = new();

    public MareWizardModule(ILogger<MareModule> logger, DiscordBotServices botServices,
        IConfigurationService<ServerConfiguration> mareClientConfigurationService,
        IConfigurationService<ServicesConfiguration> mareServicesConfiguration,
        IConnectionMultiplexer connectionMultiplexer, IDbContextFactory<MareDbContext> dbContextFactory)
    {
        _logger = logger;
        _botServices = botServices;
        _mareClientConfigurationService = mareClientConfigurationService;
        _mareServicesConfiguration = mareServicesConfiguration;
        _connectionMultiplexer = connectionMultiplexer;
        _dbContextFactory = dbContextFactory;
    }

    [ComponentInteraction("wizard-captcha:*")]
    public async Task WizardCaptcha(bool init = false)
    {
        if (!init && !(await ValidateInteraction().ConfigureAwait(false))) return;

        if (_botServices.VerifiedCaptchaUsers.Contains(Context.Interaction.User.Id))
        {
            await StartWizard(true).ConfigureAwait(false);
            return;
        }

        EmbedBuilder eb = new();

        Random rnd = new Random();
        var correctButton = rnd.Next(4) + 1;
        string nthButtonText = correctButton switch
        {
            1 => "first",
            2 => "second",
            3 => "third",
            4 => "fourth",
            _ => "unknown",
        };

        Emoji nthButtonEmoji = correctButton switch
        {
            1 => new Emoji("⬅️"),
            2 => new Emoji("🤖"),
            3 => new Emoji("‼️"),
            4 => new Emoji("✉️"),
            _ => "unknown",
        };

        eb.WithTitle("Sphene Network Authentication Protocol");
        eb.WithDescription("You are accessing this terminal interface for the first time since the network has been reinitialized." + Environment.NewLine + Environment.NewLine
            + "This terminal __requires__ visual protocols for its function. To proceed, please verify you have visual protocols enabled." + Environment.NewLine
            + $"## To verify you have visual protocols enabled __press on the **{nthButtonText}** button ({nthButtonEmoji}).__");
        eb.WithColor(Color.LightOrange);

        int incorrectButtonHighlight = 1;
        do
        {
            incorrectButtonHighlight = rnd.Next(4) + 1;
        }
        while (incorrectButtonHighlight == correctButton);

        ComponentBuilder cb = new();
        cb.WithButton("This", correctButton == 1 ? "wizard-home:false" : "wizard-captcha-fail:1", emote: new Emoji("⬅️"), style: incorrectButtonHighlight == 1 ? ButtonStyle.Primary : ButtonStyle.Secondary);
        cb.WithButton("Bot", correctButton == 2 ? "wizard-home:false" : "wizard-captcha-fail:2", emote: new Emoji("🤖"), style: incorrectButtonHighlight == 2 ? ButtonStyle.Primary : ButtonStyle.Secondary);
        cb.WithButton("Requires", correctButton == 3 ? "wizard-home:false" : "wizard-captcha-fail:3", emote: new Emoji("‼️"), style: incorrectButtonHighlight == 3 ? ButtonStyle.Primary : ButtonStyle.Secondary);
        cb.WithButton("Embeds", correctButton == 4 ? "wizard-home:false" : "wizard-captcha-fail:4", emote: new Emoji("✉️"), style: incorrectButtonHighlight == 4 ? ButtonStyle.Primary : ButtonStyle.Secondary);

        await InitOrUpdateInteraction(init, eb, cb).ConfigureAwait(false);
    }

    private async Task InitOrUpdateInteraction(bool init, EmbedBuilder eb, ComponentBuilder cb)
    {
        if (init)
        {
            await RespondAsync(embed: eb.Build(), components: cb.Build(), ephemeral: true).ConfigureAwait(false);
            var resp = await GetOriginalResponseAsync().ConfigureAwait(false);
            _botServices.ValidInteractions[Context.User.Id] = resp.Id;
            _logger.LogInformation("Init Msg: {id}", resp.Id);
        }
        else
        {
            await ModifyInteraction(eb, cb).ConfigureAwait(false);
        }
    }

    [ComponentInteraction("wizard-captcha-fail:*")]
    public async Task WizardCaptchaFail(int button)
    {
        ComponentBuilder cb = new();
        cb.WithButton("Reinitialize (with visual protocols enabled)", "wizard-captcha:false", emote: new Emoji("↩️"));
        await ((Context.Interaction) as IComponentInteraction).UpdateAsync(m =>
        {
            m.Embed = null;
            m.Content = "You pressed the wrong button. You likely have visual protocols disabled. Enable visual protocols in your Discord client (Settings -> Chat -> \"Show embeds and preview website links pasted into chat\") and try again.";
            m.Components = cb.Build();
        }).ConfigureAwait(false);

        await _botServices.LogToChannel($"{Context.User.Mention} FAILED CAPTCHA").ConfigureAwait(false);
    }


    [ComponentInteraction("wizard-home:*")]
    public async Task StartWizard(bool init = false)
    {
        if (!init && !(await ValidateInteraction().ConfigureAwait(false))) return;

        if (!_botServices.VerifiedCaptchaUsers.Contains(Context.Interaction.User.Id))
            _botServices.VerifiedCaptchaUsers.Add(Context.Interaction.User.Id);

        _logger.LogInformation("{method}:{userId}", nameof(StartWizard), Context.Interaction.User.Id);

        using var mareDb = await GetDbContext().ConfigureAwait(false);
        bool hasAccount = await mareDb.LodeStoneAuth.AnyAsync(u => u.DiscordId == Context.User.Id && u.StartedAt == null).ConfigureAwait(false);

        if (init)
        {
            bool isBanned = await mareDb.BannedRegistrations.AnyAsync(u => u.DiscordIdOrLodestoneAuth == Context.User.Id.ToString()).ConfigureAwait(false);

            if (isBanned)
            {
                EmbedBuilder ebBanned = new();
                ebBanned.WithTitle("Soul resonance disrupted");
                ebBanned.WithDescription("Your connection to the Sphene Network has been severed due to protocol violations.");
                await RespondAsync(embed: ebBanned.Build(), ephemeral: true).ConfigureAwait(false);
                return;
            }
        }
#if !DEBUG
        bool isInAprilFoolsMode = _mareServicesConfiguration.GetValueOrDefault<ulong?>(nameof(ServicesConfiguration.DiscordRoleAprilFools2024), null) != null
            && DateTime.UtcNow.Month == 4 && DateTime.UtcNow.Day == 1 && DateTime.UtcNow.Year == 2024 && DateTime.UtcNow.Hour >= 10;
#elif DEBUG
        bool isInAprilFoolsMode = true;
#endif

        EmbedBuilder eb = new();
        eb.WithTitle("Welcome to the Sphene Network Terminal");
        eb.WithDescription("Soul synchronization protocols available:" + Environment.NewLine + Environment.NewLine
            + (!hasAccount ? string.Empty : ("- Check your soul resonance status press \"ℹ️ User Info\"" + Environment.NewLine))
            + (hasAccount ? string.Empty : ("- Initialize new soul connection press \"⚛ Register\"" + Environment.NewLine))
            //+ (!hasAccount ? string.Empty : ("- Recover lost electrope key press \"🏥 Recover\"" + Environment.NewLine))
            //+ (hasAccount ? string.Empty : ("- Reestablish soul link press \"🔗 Relink\"" + Environment.NewLine))
            //+ (!hasAccount ? string.Empty : ("- Create secondary soul fragments press \"2️⃣ Secondary UID\"" + Environment.NewLine))
            //+ (!hasAccount ? string.Empty : ("- Set soul resonance identifier press \"💅 Vanity IDs\"" + Environment.NewLine))
            + (!hasAccount ? string.Empty : ("- Sever soul connections with \"⚠️ Delete\""))
            );
        eb.WithColor(Color.Blue);
        ComponentBuilder cb = new();
        if (!hasAccount)
        {
            cb.WithButton("Register", "wizard-register", ButtonStyle.Primary, new Emoji("⚛"));
            //cb.WithButton("Relink", "wizard-relink", ButtonStyle.Secondary, new Emoji("🔗"));
        }
        else
        {
            cb.WithButton("User Info", "wizard-userinfo", ButtonStyle.Secondary, new Emoji("ℹ️"));
            //cb.WithButton("Recover", "wizard-recover", ButtonStyle.Secondary, new Emoji("🏥"));
            //cb.WithButton("Secondary UID", "wizard-secondary", ButtonStyle.Secondary, new Emoji("2️⃣"));
            //cb.WithButton("Vanity IDs", "wizard-vanity", ButtonStyle.Secondary, new Emoji("💅"));
            cb.WithButton("Delete", "wizard-delete", ButtonStyle.Danger, new Emoji("⚠️"));
        }

        await InitOrUpdateInteraction(init, eb, cb).ConfigureAwait(false);
    }

    public class VanityUidModal : IModal
    {
        public string Title => "Set Vanity UID";

        [InputLabel("Set your Vanity UID")]
        [ModalTextInput("vanity_uid", TextInputStyle.Short, "5-15 characters, underscore, dash", 5, 15)]
        public string DesiredVanityUID { get; set; }
    }

    public class VanityGidModal : IModal
    {
        public string Title => "Set Vanity Syncshell ID";

        [InputLabel("Set your Vanity Syncshell ID")]
        [ModalTextInput("vanity_gid", TextInputStyle.Short, "5-20 characters, underscore, dash", 5, 20)]
        public string DesiredVanityGID { get; set; }
    }

    public class ConfirmDeletionModal : IModal
    {
        public string Title => "Confirm Account Deletion";

        [InputLabel("Enter \"DELETE\" in all Caps")]
        [ModalTextInput("confirmation", TextInputStyle.Short, "Enter DELETE")]
        public string Delete { get; set; }
    }

    private async Task<MareDbContext> GetDbContext()
    {
        return await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
    }

    private async Task<bool> ValidateInteraction()
    {
        if (Context.Interaction is not IComponentInteraction componentInteraction) return true;

        if (_botServices.ValidInteractions.TryGetValue(Context.User.Id, out ulong interactionId) && interactionId == componentInteraction.Message.Id)
        {
            return true;
        }

        EmbedBuilder eb = new();
        eb.WithTitle("Network connection lost");
        eb.WithDescription("Your terminal session has timed out. Please reinitialize the connection." + Environment.NewLine + Environment.NewLine
            + "Please use the newly started interaction or start a new one.");
        eb.WithColor(Color.Red);
        ComponentBuilder cb = new();
        await ModifyInteraction(eb, cb).ConfigureAwait(false);

        return false;
    }

    private void AddHome(ComponentBuilder cb)
    {
        cb.WithButton("Return to Home", "wizard-home:false", ButtonStyle.Secondary, new Emoji("🏠"));
    }

    private async Task ModifyModalInteraction(EmbedBuilder eb, ComponentBuilder cb)
    {
        await (Context.Interaction as SocketModal).UpdateAsync(m =>
        {
            m.Embed = eb.Build();
            m.Components = cb.Build();
        }).ConfigureAwait(false);
    }

    private async Task ModifyInteraction(EmbedBuilder eb, ComponentBuilder cb)
    {
        await ((Context.Interaction) as IComponentInteraction).UpdateAsync(m =>
        {
            m.Content = null;
            m.Embed = eb.Build();
            m.Components = cb.Build();
        }).ConfigureAwait(false);
    }

    private async Task AddUserSelection(MareDbContext mareDb, ComponentBuilder cb, string customId)
    {
        var discordId = Context.User.Id;
        var existingAuth = await mareDb.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(e => e.DiscordId == discordId).ConfigureAwait(false);
        if (existingAuth != null)
        {
            SelectMenuBuilder sb = new();
            sb.WithPlaceholder("Select a UID");
            sb.WithCustomId(customId);
            var existingUids = await mareDb.Auth.Include(u => u.User).Where(u => u.UserUID == existingAuth.User.UID || u.PrimaryUserUID == existingAuth.User.UID)
                .OrderByDescending(u => u.PrimaryUser == null).ToListAsync().ConfigureAwait(false);
            foreach (var entry in existingUids)
            {
                sb.AddOption(string.IsNullOrEmpty(entry.User.Alias) ? entry.UserUID : entry.User.Alias,
                    entry.UserUID,
                    !string.IsNullOrEmpty(entry.User.Alias) ? entry.User.UID : null,
                    entry.PrimaryUserUID == null ? new Emoji("1️⃣") : new Emoji("2️⃣"));
            }
            cb.WithSelectMenu(sb);
        }
    }

    private async Task AddGroupSelection(MareDbContext db, ComponentBuilder cb, string customId)
    {
        var primary = (await db.LodeStoneAuth.Include(u => u.User).SingleAsync(u => u.DiscordId == Context.User.Id).ConfigureAwait(false)).User;
        var secondary = await db.Auth.Include(u => u.User).Where(u => u.PrimaryUserUID == primary.UID).Select(u => u.User).ToListAsync().ConfigureAwait(false);
        var primaryGids = (await db.Groups.Include(u => u.Owner).Where(u => u.OwnerUID == primary.UID).ToListAsync().ConfigureAwait(false));
        var secondaryGids = (await db.Groups.Include(u => u.Owner).Where(u => secondary.Select(u => u.UID).Contains(u.OwnerUID)).ToListAsync().ConfigureAwait(false));
        SelectMenuBuilder gids = new();
        if (primaryGids.Any() || secondaryGids.Any())
        {
            foreach (var item in primaryGids)
            {
                gids.AddOption(item.Alias ?? item.GID, item.GID, (item.Alias == null ? string.Empty : item.GID) + $" ({item.Owner.Alias ?? item.Owner.UID})", new Emoji("1️⃣"));
            }
            foreach (var item in secondaryGids)
            {
                gids.AddOption(item.Alias ?? item.GID, item.GID, (item.Alias == null ? string.Empty : item.GID) + $" ({item.Owner.Alias ?? item.Owner.UID})", new Emoji("2️⃣"));
            }
            gids.WithCustomId(customId);
            gids.WithPlaceholder("Select a Syncshell");
            cb.WithSelectMenu(gids);
        }
    }

    private async Task<string> GenerateLodestoneAuth(ulong discordid, string hashedLodestoneId, MareDbContext dbContext)
    {
        var auth = StringUtils.GenerateRandomString(12, "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz");
        LodeStoneAuth lsAuth = new LodeStoneAuth()
        {
            DiscordId = discordid,
            HashedLodestoneId = hashedLodestoneId,
            LodestoneAuthString = auth,
            StartedAt = DateTime.UtcNow
        };

        dbContext.Add(lsAuth);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        return (auth);
    }

    private int? ParseCharacterIdFromLodestoneUrl(string lodestoneUrl)
    {
        var regex = new Regex(@"https:\/\/(na|eu|de|fr|jp)\.finalfantasyxiv\.com\/lodestone\/character\/\d+");
        var matches = regex.Match(lodestoneUrl);
        var isLodestoneUrl = matches.Success;
        if (!isLodestoneUrl || matches.Groups.Count < 1) return null;

        lodestoneUrl = matches.Groups[0].ToString();
        var stringId = lodestoneUrl.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
        if (!int.TryParse(stringId, out int lodestoneId))
        {
            return null;
        }

        return lodestoneId;
    }
}

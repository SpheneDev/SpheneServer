using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using SpheneShared.Data;
using SpheneShared.Models;
using SpheneShared.Services;
using SpheneShared.Utils;
using SpheneShared.Utils.Configuration;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using System.Text.RegularExpressions;

namespace SpheneServices.Discord;

public partial class SpheneWizardModule : InteractionModuleBase
{
    private ILogger<SpheneModule> _logger;
    private DiscordBotServices _botServices;
    private IConfigurationService<ServerConfiguration> _spheneClientConfigurationService;
    private IConfigurationService<ServicesConfiguration> _spheneServicesConfiguration;
    private IConnectionMultiplexer _connectionMultiplexer;
    private readonly IDbContextFactory<SpheneDbContext> _dbContextFactory;
    private Random random = new();

    public SpheneWizardModule(ILogger<SpheneModule> logger, DiscordBotServices botServices,
        IConfigurationService<ServerConfiguration> spheneClientConfigurationService,
        IConfigurationService<ServicesConfiguration> spheneServicesConfiguration,
        IConnectionMultiplexer connectionMultiplexer, IDbContextFactory<SpheneDbContext> dbContextFactory)
    {
        _logger = logger;
        _botServices = botServices;
        _spheneClientConfigurationService = spheneClientConfigurationService;
        _spheneServicesConfiguration = spheneServicesConfiguration;
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


    [ComponentInteraction("wizard-change-alias")]
    public async Task ChangeAlias()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;
        
        await RespondWithModalAsync<ChangeAliasModal>("wizard-change-alias-modal").ConfigureAwait(false);
    }
    
    [ModalInteraction("wizard-change-alias-modal")]
    public async Task ProcessChangeAlias(ChangeAliasModal modal)
    {
        using var spheneDb = await GetDbContext().ConfigureAwait(false);
        var discordId = Context.User.Id;
        var existingAuth = await spheneDb.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(e => e.DiscordId == discordId).ConfigureAwait(false);
        
        if (existingAuth != null && existingAuth.User != null)
        {
            EmbedBuilder eb;
            ComponentBuilder cb;
            // Überprüfen, ob der Alias bereits vergeben ist
            bool aliasExists = await spheneDb.Users.AnyAsync(u => u.Alias == modal.NewAlias && u.UID != existingAuth.User.UID).ConfigureAwait(false);
            
            if (aliasExists)
            {
                eb = new();
                eb.WithTitle("Soul Identity Conflict");
                eb.WithDescription($"The soul resonance identifier **{modal.NewAlias}** is already in use by another entity. Please choose a different identifier.");
                eb.WithColor(Color.Red);
                
                cb = new();
                cb.WithButton("Try Again", "wizard-change-alias", ButtonStyle.Primary, new Emoji("✨"));
                AddHome(cb);
                
                await ModifyModalInteraction(eb, cb).ConfigureAwait(false);
                return;
            }
            
            existingAuth.User.Alias = modal.NewAlias;
            await spheneDb.SaveChangesAsync().ConfigureAwait(false);
            
            eb = new();
            eb.WithTitle("Soul Identity Recalibrated");
            eb.WithDescription($"Your soul resonance identifier has been successfully recalibrated to: **{modal.NewAlias}**" + Environment.NewLine + Environment.NewLine + 
                "⚠️ **Important:** Please reconnect your Sphene Client once to activate the new identifier.");
            eb.WithColor(Color.Green);
            
            cb = new();
            AddHome(cb);
            
            await ModifyModalInteraction(eb, cb).ConfigureAwait(false);
            await _botServices.LogToChannel($"{Context.User.Mention} changed their alias to {modal.NewAlias}").ConfigureAwait(false);
        }
        else
        {
            EmbedBuilder eb = new();
            eb.WithTitle("Soul Resonance Error");
            eb.WithDescription("Unable to update your alias. Please ensure you have a registered account.");
            eb.WithColor(Color.Red);
            
            ComponentBuilder cb = new();
            AddHome(cb);
            
            await ModifyModalInteraction(eb, cb).ConfigureAwait(false);
        }
    }

    [ComponentInteraction("wizard-home:*")]
    public async Task StartWizard(bool init = false)
    {
        if (!init && !(await ValidateInteraction().ConfigureAwait(false))) return;

        if (!_botServices.VerifiedCaptchaUsers.Contains(Context.Interaction.User.Id))
            _botServices.VerifiedCaptchaUsers.Add(Context.Interaction.User.Id);

        _logger.LogInformation("{method}:{userId}", nameof(StartWizard), Context.Interaction.User.Id);

        using var spheneDb = await GetDbContext().ConfigureAwait(false);
        bool hasAccount = await spheneDb.LodeStoneAuth.AnyAsync(u => u.DiscordId == Context.User.Id && u.StartedAt == null).ConfigureAwait(false);
        
        string currentAlias = null;
        if (hasAccount)
        {
            var existingAuth = await spheneDb.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(e => e.DiscordId == Context.User.Id).ConfigureAwait(false);
            currentAlias = existingAuth?.User?.Alias;
        }

        if (init)
        {
            bool isBanned = await spheneDb.BannedRegistrations.AnyAsync(u => u.DiscordIdOrLodestoneAuth == Context.User.Id.ToString()).ConfigureAwait(false);

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
        bool isInAprilFoolsMode = _spheneServicesConfiguration.GetValueOrDefault<ulong?>(nameof(ServicesConfiguration.DiscordRoleAprilFools2024), null) != null
            && DateTime.UtcNow.Month == 4 && DateTime.UtcNow.Day == 1 && DateTime.UtcNow.Year == 2024 && DateTime.UtcNow.Hour >= 10;
#elif DEBUG
        bool isInAprilFoolsMode = true;
#endif

        EmbedBuilder eb = new();
        eb.WithTitle("Welcome to the Sphene Network Terminal");
        string description = "Soul synchronization protocols available:" + Environment.NewLine;
        
        if (!string.IsNullOrEmpty(currentAlias))
        {
            description += $"Current Soul Resonance Identifier: **{currentAlias}**" + Environment.NewLine;
        }
        
        description += Environment.NewLine
            + (!hasAccount ? string.Empty : ("- Check your soul resonance status press \"ℹ️ User Info\"" + Environment.NewLine))
            + (hasAccount ? string.Empty : ("- Initialize new soul connection press \"⚛ Register\"" + Environment.NewLine))
            + (!hasAccount ? string.Empty : ("- Recalibrate your soul resonance identifier press \"✨ Soul Identity\"" + Environment.NewLine))
            + (!hasAccount ? string.Empty : ("- Generate a new electrope key press \"🔑 New Key\"" + Environment.NewLine))
            + (!hasAccount ? string.Empty : ("- Sever soul connections with \"⚠️ Delete\""));
        
        eb.WithDescription(description);
        eb.WithColor(Color.Blue);
        ComponentBuilder cb = new();
        if (!hasAccount)
        {
            cb.WithButton("Register", "wizard-register", ButtonStyle.Primary, new Emoji("⚛"));
        }
        else
        {
            cb.WithButton("User Info", "wizard-userinfo", ButtonStyle.Secondary, new Emoji("ℹ️"));
            cb.WithButton("Soul Identity", "wizard-change-alias", ButtonStyle.Secondary, new Emoji("✨"));
            cb.WithButton("New Key", "wizard-newkey", ButtonStyle.Secondary, new Emoji("🔑"));
            cb.WithButton("Delete", "wizard-delete", ButtonStyle.Danger, new Emoji("⚠️"));
        }

        await InitOrUpdateInteraction(init, eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-newkey")]
    public async Task RegenerateSecretKey()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        using var spheneDb = await GetDbContext().ConfigureAwait(false);
        var discordId = Context.User.Id;
        var existingAuth = await spheneDb.LodeStoneAuth.Include(u => u.User)
            .SingleOrDefaultAsync(e => e.DiscordId == discordId).ConfigureAwait(false);

        EmbedBuilder eb = new();
        ComponentBuilder cb = new();

        if (existingAuth == null || existingAuth.User == null)
        {
            eb.WithTitle("No account found");
            eb.WithDescription("You do not have a registered account. Please use Register to initialize your soul connection.");
            eb.WithColor(Color.Red);
            AddHome(cb);
            await ModifyInteraction(eb, cb).ConfigureAwait(false);
            return;
        }

        // Remove existing primary auth key if present
        var primaryAuth = await spheneDb.Auth.Include(a => a.User)
            .SingleOrDefaultAsync(a => a.UserUID == existingAuth.User.UID && a.PrimaryUserUID == null).ConfigureAwait(false);
        if (primaryAuth != null)
        {
            spheneDb.Auth.Remove(primaryAuth);
            await spheneDb.SaveChangesAsync().ConfigureAwait(false);
        }

        // Generate a new key using the same logic as registration
        var originalSecretKeyTime = StringUtils.GenerateRandomString(64) + DateTime.UtcNow.ToString();
        var originalSecretKey = StringUtils.Sha256String(originalSecretKeyTime);
        var clientHashedKey = StringUtils.Sha256String(originalSecretKey);
        string databaseHashedKey = StringUtils.Sha256String(clientHashedKey);

        var newAuth = new Auth()
        {
            HashedKey = databaseHashedKey,
            User = existingAuth.User,
        };

        await spheneDb.Auth.AddAsync(newAuth).ConfigureAwait(false);
        await spheneDb.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogDebug("Regenerated key for user: {userUID}:{hashedKey}", existingAuth.User.UID, databaseHashedKey);
        await _botServices.LogToChannel($"{Context.User.Mention} NEW KEY GENERATED: => {existingAuth.User.UID}").ConfigureAwait(false);

        eb.WithColor(Color.Green);
        eb.WithTitle($"New electrope key generated for UID: {existingAuth.User.UID}");
        eb.WithDescription("This is your private electrope key. Do not share this electrope key with anyone. **If you lose it, it is irrevocably lost.**"
                                     + Environment.NewLine + Environment.NewLine
                                     + "**__NOTE: Electrope keys are considered legacy. Using the suggested OAuth2 authentication in Sphene, you do not need to use this Electrope Key.__**"
                                     + Environment.NewLine + Environment.NewLine
                                     + $"||**`{originalSecretKey}`**||"
                                     + Environment.NewLine + Environment.NewLine
                                     + "If you want to continue using legacy authentication, enter this key in Sphene Synchronos and hit save to connect to the network."
                                     + Environment.NewLine
                                     + "__NOTE: The Electrope Key only contains the letters ABCDEF and numbers 0 - 9.__"
                                     + Environment.NewLine
                                     + "You should connect as soon as possible to not get caught by the automatic cleanup process."
                                     + Environment.NewLine
                                     + "May your soul resonate with others.");
        AddHome(cb);
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    private async Task<SpheneDbContext> GetDbContext()
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

    private async Task AddUserSelection(SpheneDbContext spheneDb, ComponentBuilder cb, string customId)
    {
        var discordId = Context.User.Id;
        var existingAuth = await spheneDb.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(e => e.DiscordId == discordId).ConfigureAwait(false);
        if (existingAuth != null)
        {
            SelectMenuBuilder sb = new();
            sb.WithPlaceholder("Select a UID");
            sb.WithCustomId(customId);
            var existingUids = await spheneDb.Auth.Include(u => u.User).Where(u => u.UserUID == existingAuth.User.UID || u.PrimaryUserUID == existingAuth.User.UID)
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

    private async Task<string> GenerateLodestoneAuth(ulong discordid, string hashedLodestoneId, SpheneDbContext dbContext)
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

public class ConfirmDeletionModal : IModal
{
    public string Title => "Confirm Account Deletion";

    [InputLabel("Enter \"DELETE\" in all Caps")]
    [ModalTextInput("confirmation", TextInputStyle.Short, "Enter DELETE")]
    public string Delete { get; set; }
}

public class ChangeAliasModal : IModal
{
    public string Title => "Soul Identity Recalibration";

    [InputLabel("Enter your new soul identifier")]
    [ModalTextInput("new_alias", TextInputStyle.Short, "5-15 characters", 5, 15)]
    public string NewAlias { get; set; }
}

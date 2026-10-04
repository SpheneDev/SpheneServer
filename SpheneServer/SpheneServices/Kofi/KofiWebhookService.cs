using System.Text.Json;
using System.Text.Json.Serialization;
using Discord.Rest;
using Microsoft.EntityFrameworkCore;
using SpheneShared.Data;
using SpheneShared.Services;
using SpheneShared.Utils.Configuration;
using SpheneServices.Discord;

namespace SpheneServices.Kofi;

public sealed class KofiWebhookService
{
    private readonly ILogger<KofiWebhookService> _logger;
    private readonly IConfigurationService<ServicesConfiguration> _configuration;
    private readonly DiscordBotServices _botServices;
    private readonly IDbContextFactory<SpheneDbContext> _dbContextFactory;

    public KofiWebhookService(
        ILogger<KofiWebhookService> logger,
        IConfigurationService<ServicesConfiguration> configuration,
        DiscordBotServices botServices,
        IDbContextFactory<SpheneDbContext> dbContextFactory)
    {
        _logger = logger;
        _configuration = configuration;
        _botServices = botServices;
        _dbContextFactory = dbContextFactory;
    }

    /// <summary>
    /// Processes a Ko-fi webhook payload and assigns the supporter role if the donor can be matched to a Discord user.
    /// </summary>
    /// <param name="rawData">The raw JSON string from the Ko-fi webhook "data" field.</param>
    /// <returns>True if the webhook was processed successfully, false otherwise.</returns>
    public async Task<bool> ProcessWebhookAsync(string rawData)
    {
        if (string.IsNullOrWhiteSpace(rawData))
        {
            _logger.LogWarning("Ko-fi webhook received with empty data");
            return false;
        }

        KofiWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<KofiWebhookPayload>(rawData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize Ko-fi webhook payload");
            return false;
        }

        if (payload == null)
        {
            _logger.LogWarning("Ko-fi webhook payload was null after deserialization");
            return false;
        }

        var expectedToken = _configuration.GetValueOrDefault(nameof(ServicesConfiguration.KofiWebhookToken), string.Empty);
        if (string.IsNullOrEmpty(expectedToken))
        {
            _logger.LogWarning("KofiWebhookToken not configured, ignoring webhook");
            return false;
        }

        if (!string.Equals(payload.VerificationToken, expectedToken, StringComparison.Ordinal))
        {
            _logger.LogWarning("Ko-fi webhook verification token mismatch");
            return false;
        }

        _logger.LogInformation("Processing Ko-fi webhook: Type={type}, FromName={fromName}, Amount={amount}, Email={email}",
            payload.Type, payload.FromName, payload.Amount, payload.Email);

        var discordId = await TryMatchDiscordUserAsync(payload).ConfigureAwait(false);
        if (discordId == null)
        {
            _logger.LogWarning("Could not match Ko-fi donor to Discord user. FromName={fromName}, Message={message}, Email={email}",
                payload.FromName, payload.Message, payload.Email);
            await _botServices.LogToChannel(
                $"Ko-fi donation received from '{payload.FromName}' ({payload.Amount} {payload.Currency}) " +
                $"but could not match to a Discord user. Message: '{payload.Message}'. " +
                $"Ask the donor to include their Discord username or ID in the Ko-fi message.").ConfigureAwait(false);
            return true;
        }

        await _botServices.AddSupporterRoleAsync(discordId.Value).ConfigureAwait(false);

        await UpdateSupporterStatusInDbAsync(discordId.Value, true).ConfigureAwait(false);

        await _botServices.LogToChannel(
            $"Ko-fi donation from '{payload.FromName}' ({payload.Amount} {payload.Currency}) — " +
            $"matched to Discord user {discordId.Value}. Supporter role assigned.").ConfigureAwait(false);

        _logger.LogInformation("Supporter role assigned to Discord user {discordId} from Ko-fi donation", discordId.Value);
        return true;
    }

    /// <summary>
    /// Attempts to match a Ko-fi donor to a Discord user by:
    /// 1. Parsing a Discord ID or username from the donation message
    /// 2. Matching the from_name to a guild member's username or nickname
    /// </summary>
    private async Task<ulong?> TryMatchDiscordUserAsync(KofiWebhookPayload payload)
    {
        var guild = _botServices.GetGuild();
        if (guild == null)
        {
            _logger.LogWarning("No Discord guild available for Ko-fi matching");
            return null;
        }

        ulong? matchedId = null;

        if (!string.IsNullOrWhiteSpace(payload.Message))
        {
            matchedId = await TryMatchByMessageAsync(guild, payload.Message).ConfigureAwait(false);
        }

        if (matchedId == null && !string.IsNullOrWhiteSpace(payload.FromName))
        {
            matchedId = await TryMatchByNameAsync(guild, payload.FromName).ConfigureAwait(false);
        }

        if (matchedId != null)
        {
            var isRegistered = await IsDiscordUserRegisteredAsync(matchedId.Value).ConfigureAwait(false);
            if (!isRegistered)
            {
                _logger.LogWarning("Matched Discord user {discordId} but they are not registered in Sphene", matchedId);
                return null;
            }
        }

        return matchedId;
    }

    private static async Task<ulong?> TryMatchByMessageAsync(RestGuild guild, string message)
    {
        var trimmed = message.Trim();

        if (ulong.TryParse(trimmed, out var parsedId))
        {
            var user = await guild.GetUserAsync(parsedId).ConfigureAwait(false);
            if (user != null) return parsedId;
        }

        var mentionMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"<@!?(\d+)>");
        if (mentionMatch.Success && ulong.TryParse(mentionMatch.Groups[1].Value, out var mentionedId))
        {
            var user = await guild.GetUserAsync(mentionedId).ConfigureAwait(false);
            if (user != null) return mentionedId;
        }

        return null;
    }

    private static async Task<ulong?> TryMatchByNameAsync(RestGuild guild, string fromName)
    {
        var trimmed = fromName.Trim();

        await foreach (var userBatch in guild.GetUsersAsync().ConfigureAwait(false))
        {
            foreach (var user in userBatch)
            {
                if (string.Equals(user.Username, trimmed, StringComparison.OrdinalIgnoreCase))
                    return user.Id;
                if (user.GlobalName != null && string.Equals(user.GlobalName, trimmed, StringComparison.OrdinalIgnoreCase))
                    return user.Id;
                if (user.DisplayName != null && string.Equals(user.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase))
                    return user.Id;
            }
        }

        return null;
    }

    private async Task<bool> IsDiscordUserRegisteredAsync(ulong discordId)
    {
        using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await dbContext.LodeStoneAuth.AnyAsync(la => la.DiscordId == discordId).ConfigureAwait(false);
    }

    private async Task UpdateSupporterStatusInDbAsync(ulong discordId, bool isSupporter)
    {
        using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var lodestoneAuth = await dbContext.LodeStoneAuth
            .Include(la => la.User)
            .SingleOrDefaultAsync(la => la.DiscordId == discordId)
            .ConfigureAwait(false);
        if (lodestoneAuth?.User == null)
        {
            _logger.LogWarning("Cannot update supporter status: no LodeStoneAuth found for Discord ID {discordId}", discordId);
            return;
        }
        if (lodestoneAuth.User.IsSupporter == isSupporter)
            return;
        lodestoneAuth.User.IsSupporter = isSupporter;
        dbContext.Users.Update(lodestoneAuth.User);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        _logger.LogInformation("User {uid} supporter status updated to {isSupporter} in DB", lodestoneAuth.User.UID, isSupporter);
    }
}

public sealed class KofiWebhookPayload
{
    [JsonPropertyName("verification_token")]
    public string? VerificationToken { get; set; }

    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("is_public")]
    public bool IsPublic { get; set; }

    [JsonPropertyName("from_name")]
    public string? FromName { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("amount")]
    public string? Amount { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("is_subscription_payment")]
    public bool IsSubscriptionPayment { get; set; }

    [JsonPropertyName("is_first_subscription_payment")]
    public bool IsFirstSubscriptionPayment { get; set; }

    [JsonPropertyName("kofi_transaction_id")]
    public string? KofiTransactionId { get; set; }

    [JsonPropertyName("tier_name")]
    public string? TierName { get; set; }

    [JsonPropertyName("shop_items")]
    public object? ShopItems { get; set; }

    [JsonPropertyName("shipping")]
    public object? Shipping { get; set; }
}

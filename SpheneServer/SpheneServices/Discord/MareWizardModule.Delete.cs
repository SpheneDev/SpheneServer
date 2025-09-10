using Discord.Interactions;
using Discord;
using SpheneShared.Utils;
using SpheneShared.Utils.Configuration;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;

namespace SpheneServices.Discord;

public partial class SpheneWizardModule
{
    [ComponentInteraction("wizard-delete")]
    public async Task ComponentDelete()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentDelete), Context.Interaction.User.Id);

        using var spheneDb = await GetDbContext().ConfigureAwait(false);
        EmbedBuilder eb = new();
        eb.WithTitle("Sever Soul Connection");
        eb.WithDescription("You can sever your primary or secondary soul connections here." + Environment.NewLine + Environment.NewLine
            + "__Note: severing your primary soul will delete all associated secondary soul fragments as well.__" + Environment.NewLine + Environment.NewLine
            + "- 1️⃣ is your primary soul/UID" + Environment.NewLine
            + "- 2️⃣ are all your secondary soul fragments/UIDs" + Environment.NewLine
            + "If you are using Vanity UIDs the original UID is displayed in the second line of the soul selection.");
        eb.WithColor(Color.Blue);

        ComponentBuilder cb = new();
        await AddUserSelection(spheneDb, cb, "wizard-delete-select").ConfigureAwait(false);
        AddHome(cb);
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-delete-select")]
    public async Task SelectionDeleteAccount(string uid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(SelectionDeleteAccount), Context.Interaction.User.Id, uid);

        using var spheneDb = await GetDbContext().ConfigureAwait(false);
        bool isPrimary = spheneDb.Auth.Single(u => u.UserUID == uid).PrimaryUserUID == null;
        EmbedBuilder eb = new();
        eb.WithTitle($"Are you sure you want to sever soul connection {uid}?");
        eb.WithDescription($"This operation is irreversible. All your soul bonds, joined syncshells and information stored in the network for {uid} will be " +
            $"irrevocably severed." +
            (isPrimary ? (Environment.NewLine + Environment.NewLine +
            "⚠️ **You are about to sever a Primary Soul, all attached Secondary Soul Fragments and their information will be severed as well.** ⚠️") : string.Empty));
        eb.WithColor(Color.Purple);
        ComponentBuilder cb = new();
        cb.WithButton("Cancel", "wizard-delete", emote: new Emoji("❌"));
        cb.WithButton($"Sever {uid}", "wizard-delete-confirm:" + uid, ButtonStyle.Danger, emote: new Emoji("🗑️"));
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-delete-confirm:*")]
    public async Task ComponentDeleteAccountConfirm(string uid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(ComponentDeleteAccountConfirm), Context.Interaction.User.Id, uid);

        await RespondWithModalAsync<ConfirmDeletionModal>("wizard-delete-confirm-modal:" + uid).ConfigureAwait(false);
    }

    [ModalInteraction("wizard-delete-confirm-modal:*")]
    public async Task ModalDeleteAccountConfirm(string uid, ConfirmDeletionModal modal)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(ModalDeleteAccountConfirm), Context.Interaction.User.Id, uid);

        try
        {
            if (!string.Equals("DELETE", modal.Delete, StringComparison.Ordinal))
            {
                EmbedBuilder eb = new();
                eb.WithTitle("Soul severance not confirmed properly");
                eb.WithDescription($"You entered {modal.Delete} but requested was DELETE. Please try again and enter DELETE to confirm the soul severance.");
                eb.WithColor(Color.Red);
                ComponentBuilder cb = new();
                cb.WithButton("Cancel", "wizard-delete", emote: new Emoji("❌"));
                cb.WithButton("Retry", "wizard-delete-confirm:" + uid, emote: new Emoji("🔁"));

                await ModifyModalInteraction(eb, cb).ConfigureAwait(false);
            }
            else
            {
                var maxGroupsByUser = _spheneClientConfigurationService.GetValueOrDefault(nameof(ServerConfiguration.MaxGroupUserCount), 3);

                using var db = await GetDbContext().ConfigureAwait(false);
                var user = await db.Users.SingleAsync(u => u.UID == uid).ConfigureAwait(false);
                var lodestone = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(u => u.User.UID == uid).ConfigureAwait(false);
                await SharedDbFunctions.PurgeUser(_logger, user, db, maxGroupsByUser).ConfigureAwait(false);

                EmbedBuilder eb = new();
                eb.WithTitle($"Soul connection {uid} successfully severed");
                eb.WithColor(Color.Green);
                ComponentBuilder cb = new();
                AddHome(cb);

                await ModifyModalInteraction(eb, cb).ConfigureAwait(false);

                await _botServices.LogToChannel($"{Context.User.Mention} DELETE SUCCESS: {uid}").ConfigureAwait(false);

                // only remove role if deleted uid has lodestone attached (== primary uid)
                if (lodestone != null)
                {
                    await _botServices.RemoveRegisteredRoleAsync(Context.Interaction.User).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling modal delete account confirm");
        }
    }
}

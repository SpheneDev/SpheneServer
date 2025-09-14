using Discord.Interactions;
using Discord;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using System.Text;

namespace SpheneServices.Discord;

public partial class SpheneWizardModule
{
    [ComponentInteraction("wizard-vanity")]
    public async Task ComponentVanity()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentVanity), Context.Interaction.User.Id);

        StringBuilder sb = new();
        var user = await Context.Guild.GetUserAsync(Context.User.Id).ConfigureAwait(false);
        bool userIsInVanityRole = _botServices.VanityRoles.Keys.Any(u => user.RoleIds.Contains(u.Id)) || !_botServices.VanityRoles.Any();
        if (!userIsInVanityRole)
        {
            sb.AppendLine("To be able to set Soul Resonance Identifiers you must have one of the following roles:");
            foreach (var role in _botServices.VanityRoles)
            {
                sb.Append("- ").Append(role.Key.Mention).Append(" (").Append(role.Value).AppendLine(")");
            }
        }
        else
        {
            sb.AppendLine("Your current roles on this server allow you to set Soul Resonance Identifiers.");
        }

        EmbedBuilder eb = new();
        eb.WithTitle("Soul Resonance Identifiers");
        eb.WithDescription("You are able to set your Soul Resonance Identifiers here." + Environment.NewLine
            + "Soul Resonance Identifiers are a way to customize your displayed UID or Syncshell ID to other souls." + Environment.NewLine + Environment.NewLine
            + sb.ToString());
        eb.WithColor(Color.Blue);
        ComponentBuilder cb = new();
        AddHome(cb);
        if (userIsInVanityRole)
        {
            using var db = await GetDbContext().ConfigureAwait(false);
            await AddUserSelection(db, cb, "wizard-vanity-uid").ConfigureAwait(false);
            await AddGroupSelection(db, cb, "wizard-vanity-gid").ConfigureAwait(false);
        }

        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-vanity-uid")]
    public async Task SelectionVanityUid(string uid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(SelectionVanityUid), Context.Interaction.User.Id, uid);

        using var db = await GetDbContext().ConfigureAwait(false);
        var user = db.Users.Single(u => u.UID == uid);
        EmbedBuilder eb = new();
        eb.WithColor(Color.Purple);
        eb.WithTitle($"Set Soul Resonance Identifier for {uid}");
        eb.WithDescription($"You are about to change the Soul Resonance Identifier for {uid}" + Environment.NewLine + Environment.NewLine
            + "The current Soul Resonance Identifier is set to: **" + (user.Alias == null ? "No Soul Resonance Identifier set" : user.Alias) + "**");
        ComponentBuilder cb = new();
        cb.WithButton("Cancel", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
        cb.WithButton("Set Soul Identifier", "wizard-vanity-uid-set:" + uid, ButtonStyle.Primary, new Emoji("💅"));

        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-vanity-uid-set:*")]
    public async Task SelectionVanityUidSet(string uid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(SelectionVanityUidSet), Context.Interaction.User.Id, uid);

        await RespondWithModalAsync<VanityUidModal>("wizard-vanity-uid-modal:" + uid).ConfigureAwait(false);
    }

    [ModalInteraction("wizard-vanity-uid-modal:*")]
    public async Task ConfirmVanityUidModal(string uid, VanityUidModal modal)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}:{vanity}", nameof(ConfirmVanityUidModal), Context.Interaction.User.Id, uid, modal.DesiredVanityUID);

        EmbedBuilder eb = new();
        ComponentBuilder cb = new();
        var desiredVanityUid = modal.DesiredVanityUID;
        using var db = await GetDbContext().ConfigureAwait(false);
        bool canAddVanityId = !db.Users.Any(u => u.UID == modal.DesiredVanityUID || u.Alias == modal.DesiredVanityUID);

        Regex rgx = new(@"^[_\-a-zA-Z0-9]{5,15}$", RegexOptions.ECMAScript);
        if (!rgx.Match(desiredVanityUid).Success)
        {
            eb.WithColor(Color.Red);
            eb.WithTitle("Invalid Soul Resonance Identifier");
            eb.WithDescription("A Soul Resonance Identifier must be between 5 and 15 characters long and only contain the letters A-Z, numbers 0-9, dashes (-) and underscores (_).");
            cb.WithButton("Cancel", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
            cb.WithButton("Pick Different Identifier", "wizard-vanity-uid-set:" + uid, ButtonStyle.Primary, new Emoji("💅"));
        }
        else if (!canAddVanityId)
        {
            eb.WithColor(Color.Red);
            eb.WithTitle("Soul Resonance Identifier already taken");
            eb.WithDescription($"The Soul Resonance Identifier {desiredVanityUid} has already been claimed. Please pick a different one.");
            cb.WithButton("Cancel", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
            cb.WithButton("Pick Different UID", "wizard-vanity-uid-set:" + uid, ButtonStyle.Primary, new Emoji("💅"));
        }
        else
        {
            var user = await db.Users.SingleAsync(u => u.UID == uid).ConfigureAwait(false);
            user.Alias = desiredVanityUid;
            db.Update(user);
            await db.SaveChangesAsync().ConfigureAwait(false);
            eb.WithColor(Color.Green);
            eb.WithTitle("Soul Resonance Identifier successfully set");
            eb.WithDescription($"Your Soul Resonance Identifier for \"{uid}\" was successfully changed to \"{desiredVanityUid}\"."+Environment.NewLine+Environment.NewLine
                +"For changes to take effect you need to reconnect to the Sphene service.");
            await _botServices.LogToChannel($"{Context.User.Mention} VANITY UID SET: UID: {user.UID}, Vanity: {desiredVanityUid}").ConfigureAwait(false);
            AddHome(cb);
        }

        await ModifyModalInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-vanity-gid")]
    public async Task SelectionVanityGid(string gid)
    {
        _logger.LogInformation("{method}:{userId}:{uid}", nameof(SelectionVanityGid), Context.Interaction.User.Id, gid);

        using var db = await GetDbContext().ConfigureAwait(false);
        var group = db.Groups.Single(u => u.GID == gid);
        EmbedBuilder eb = new();
        eb.WithColor(Color.Purple);
        eb.WithTitle($"Set Soul Syncshell Identifier for {gid}");
        eb.WithDescription($"You are about to change the Soul Syncshell Identifier for {gid}" + Environment.NewLine + Environment.NewLine
            + "The current Soul Syncshell Identifier is set to: **" + (group.Alias == null ? "No Soul Syncshell Identifier set" : group.Alias) + "**");
        ComponentBuilder cb = new();
        cb.WithButton("Cancel", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
        cb.WithButton("Set Syncshell Identifier", "wizard-vanity-gid-set:" + gid, ButtonStyle.Primary, new Emoji("💅"));

        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-vanity-gid-set:*")]
    public async Task SelectionVanityGidSet(string gid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{gid}", nameof(SelectionVanityGidSet), Context.Interaction.User.Id, gid);

        await RespondWithModalAsync<VanityGidModal>("wizard-vanity-gid-modal:" + gid).ConfigureAwait(false);
    }

    [ModalInteraction("wizard-vanity-gid-modal:*")]
    public async Task ConfirmVanityGidModal(string gid, VanityGidModal modal)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{gid}:{vanity}", nameof(ConfirmVanityGidModal), Context.Interaction.User.Id, gid, modal.DesiredVanityGID);

        EmbedBuilder eb = new();
        ComponentBuilder cb = new();
        var desiredVanityGid = modal.DesiredVanityGID;
        using var db = await GetDbContext().ConfigureAwait(false);
        bool canAddVanityId = !db.Groups.Any(u => u.GID == modal.DesiredVanityGID || u.Alias == modal.DesiredVanityGID);

        Regex rgx = new(@"^[_\-a-zA-Z0-9]{5,20}$", RegexOptions.ECMAScript);
        if (!rgx.Match(desiredVanityGid).Success)
        {
            eb.WithColor(Color.Red);
            eb.WithTitle("Invalid Soul Syncshell Identifier");
            eb.WithDescription("A Soul Syncshell Identifier must be between 5 and 20 characters long and only contain the letters A-Z, numbers 0-9, dashes (-) and underscores (_).");
            cb.WithButton("Cancel", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
            cb.WithButton("Pick Different Identifier", "wizard-vanity-gid-set:" + gid, ButtonStyle.Primary, new Emoji("💅"));
        }
        else if (!canAddVanityId)
        {
            eb.WithColor(Color.Red);
            eb.WithTitle("Soul Syncshell Identifier already taken");
            eb.WithDescription($"The Soul Syncshell Identifier \"{desiredVanityGid}\" has already been claimed. Please pick a different one.");
            cb.WithButton("Cancel", "wizard-vanity", ButtonStyle.Secondary, emote: new Emoji("❌"));
            cb.WithButton("Pick Different ID", "wizard-vanity-gid-set:" + gid, ButtonStyle.Primary, new Emoji("💅"));
        }
        else
        {
            var group = await db.Groups.SingleAsync(u => u.GID == gid).ConfigureAwait(false);
            group.Alias = desiredVanityGid;
            db.Update(group);
            await db.SaveChangesAsync().ConfigureAwait(false);
            eb.WithColor(Color.Green);
            eb.WithTitle("Soul Syncshell Identifier successfully set");
            eb.WithDescription($"Your Soul Syncshell Identifier for {gid} was successfully changed to \"{desiredVanityGid}\"."+Environment.NewLine+Environment.NewLine
                +"For changes to take effect you need to reconnect to the Sphene service.");
            AddHome(cb);
            await _botServices.LogToChannel($"{Context.User.Mention} VANITY GID SET: GID: {group.GID}, Vanity: {desiredVanityGid}").ConfigureAwait(false);
        }

        await ModifyModalInteraction(eb, cb).ConfigureAwait(false);
    }
}

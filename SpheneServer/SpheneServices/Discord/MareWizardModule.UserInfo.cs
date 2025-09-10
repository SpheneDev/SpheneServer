using Discord.Interactions;
using Discord;
using SpheneShared.Data;
using Microsoft.EntityFrameworkCore;

namespace SpheneServices.Discord;

public partial class SpheneWizardModule
{
    [ComponentInteraction("wizard-userinfo")]
    public async Task ComponentUserinfo()
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}", nameof(ComponentUserinfo), Context.Interaction.User.Id);

        using var spheneDb = await GetDbContext().ConfigureAwait(false);
        EmbedBuilder eb = new();
        eb.WithTitle("Soul Profile Info");
        eb.WithColor(Color.Blue);
        eb.WithDescription("You can see information about your soul connection(s) here." + Environment.NewLine
            + "Use the selection below to select a soul connection to see info for." //+ Environment.NewLine + Environment.NewLine
            //+ "- 1️⃣ is your primary soul connection/UID" + Environment.NewLine
            //+ "- 2️⃣ are all your secondary soul fragments/UIDs" + Environment.NewLine
            //+ "If you are using Soul Resonance Identifiers the original UID is displayed in the second line of the selection."
            );
        ComponentBuilder cb = new();
        await AddUserSelection(spheneDb, cb, "wizard-userinfo-select").ConfigureAwait(false);
        AddHome(cb);
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    [ComponentInteraction("wizard-userinfo-select")]
    public async Task SelectionUserinfo(string uid)
    {
        if (!(await ValidateInteraction().ConfigureAwait(false))) return;

        _logger.LogInformation("{method}:{userId}:{uid}", nameof(SelectionUserinfo), Context.Interaction.User.Id, uid);

        using var spheneDb = await GetDbContext().ConfigureAwait(false);
        EmbedBuilder eb = new();
        eb.WithTitle($"Soul Profile Info for {uid}");
        await HandleUserInfo(eb, spheneDb, uid).ConfigureAwait(false);
        eb.WithColor(Color.Green);
        ComponentBuilder cb = new();
        await AddUserSelection(spheneDb, cb, "wizard-userinfo-select").ConfigureAwait(false);
        AddHome(cb);
        await ModifyInteraction(eb, cb).ConfigureAwait(false);
    }

    private async Task HandleUserInfo(EmbedBuilder eb, SpheneDbContext db, string uid)
    {
        ulong userToCheckForDiscordId = Context.User.Id;

        var dbUser = await db.Users.SingleOrDefaultAsync(u => u.UID == uid).ConfigureAwait(false);

        var groups = await db.Groups.Where(g => g.OwnerUID == dbUser.UID).ToListAsync().ConfigureAwait(false);
        var groupsJoined = await db.GroupPairs.Where(g => g.GroupUserUID == dbUser.UID).ToListAsync().ConfigureAwait(false);
        var identity = await _connectionMultiplexer.GetDatabase().StringGetAsync("UID:" + dbUser.UID).ConfigureAwait(false);

        eb.WithDescription("This is the soul profile info for your selected UID. You can check other UIDs or go back using the menu below.");
        if (!string.IsNullOrEmpty(dbUser.Alias))
        {
            eb.AddField("Soul Resonance Identifier", dbUser.Alias);
        }
        eb.AddField("Last Online (UTC)", dbUser.LastLoggedIn.ToString("U"));
        eb.AddField("Currently online ", !string.IsNullOrEmpty(identity));
        eb.AddField("Joined Syncshells", groupsJoined.Count);
        eb.AddField("Owned Syncshells", groups.Count);
        foreach (var group in groups)
        {
            var syncShellUserCount = await db.GroupPairs.CountAsync(g => g.GroupGID == group.GID).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(group.Alias))
            {
                eb.AddField("Owned Syncshell " + group.GID + " Soul Syncshell Identifier", group.Alias);
            }
            eb.AddField("Owned Syncshell " + group.GID + " User Count", syncShellUserCount);
        }
    }

}

using Discord;
using Discord.Interactions;

namespace MareSynchronosServices.Discord;

// todo: remove all this crap at some point

public class LodestoneModal : IModal
{
    public string Title => "Soul Resonance Verification";

    [InputLabel("Enter the Lodestone URL of your Soul's Avatar")]
    [ModalTextInput("lodestone_url", TextInputStyle.Short, "https://*.finalfantasyxiv.com/lodestone/character/<CHARACTERID>/")]
    public string LodestoneUrl { get; set; }
}

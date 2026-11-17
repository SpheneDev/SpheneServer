namespace SpheneShared.Utils;

/// <summary>
/// Private-chat GIF capability policy. Effective access requires the GIF
/// service to be enabled AND (public release enabled OR the user's
/// per-user beta flag) AND a provider key being configured. Evaluated at
/// connection time into <c>ServerInfo.GifEnabled</c> /
/// <c>ServerInfo.GifApiKey</c> — the capability flag and the credential
/// are only ever shipped together, so unauthorized users can neither
/// search GIFs nor obtain the Sphene-controlled provider key.
///
/// Honest boundary: the server never sees sealed message plaintext, so a
/// modified client could still encode a GIF reference as ordinary E2EE
/// text. That is inherent to E2EE and deliberately not defended at the
/// transport layer - the gate controls capability, not ciphertext.
/// </summary>
public static class GifAccessPolicy
{
    /// <summary>Effective GIF access for a connecting user.</summary>
    public static bool CanUse(bool serviceEnabled, bool publicEnabled,
        bool betaEnabled, string apiKey)
        => serviceEnabled
            && (publicEnabled || betaEnabled)
            && !string.IsNullOrEmpty(apiKey);
}

namespace GhostDeck;

/// <summary>
/// Network policy of the SummitPro fork (branch summitpro-1596).
/// <see cref="Offline"/> follows the "Offline mode" setting (on by default): when on, nothing
/// talks to the network - no update check, no model-database download, no announcements,
/// no statistics. <see cref="InstallDownloadedUpdates"/> is a hard compile-time switch: this
/// fork never replaces itself with an upstream release, whatever the setting says.
/// </summary>
public static class NetPolicy
{
    /// <summary>Set from <c>AppSettings.OfflineMode</c> at startup and when the setting changes.</summary>
    public static volatile bool Offline = true;

    /// <summary>Never download-and-run an upstream release over this fork.</summary>
    public static readonly bool InstallDownloadedUpdates = false;
}

namespace ArcaneCore.Kernel.Configuration;

/// <summary>Configuration for the realm (logon) daemon's console (docs/areas/server-console.md).</summary>
[ConfigDaemons(ConfigDaemon.Realm)]
public sealed class RealmConsoleOptions
{
    public const string SectionName = "Realm:Console";

    /// <summary>
    /// Accept typed console commands (help, info, realmlist, online, account ..., quit) when standard input is an
    /// interactive console. Never starts a reader when input is redirected (services, tests, deploy scripts). Default on.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

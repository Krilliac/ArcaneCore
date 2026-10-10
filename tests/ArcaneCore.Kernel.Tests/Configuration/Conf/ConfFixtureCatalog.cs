using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Conf;

namespace ArcaneCore.Kernel.Tests.Configuration.Conf;

/// <summary>A small hand-built catalog, so the .conf tooling is tested without the production options classes.</summary>
internal static class ConfFixtureCatalog
{
    public static readonly string[] ModeValues = ["Off", "Fast"];

    private static ConfKey Scalar(string path, string type, string? def, ConfigDaemon daemons, string reload = "live", string[]? enumValues = null)
        => new(path, path[..path.LastIndexOf(':')], type, ConfKey.Scalar, type, enumValues, def, [], daemons, reload,
            "Meaning of " + path + ".", ConfValue.AllowedText(type, enumValues), "docs/reference/configuration.md#" + path[..path.IndexOf(':')].ToLowerInvariant());

    public static ConfCatalog Create() => new(
    [
        Scalar("Fixture:Port", "int", "8085", ConfigDaemon.World),
        Scalar("Fixture:Name", "string", "ArcaneCore", ConfigDaemon.World, "restart"),
        Scalar("Fixture:Enabled", "bool", "true", ConfigDaemon.World),
        Scalar("Fixture:Ratio", "float", "0.1", ConfigDaemon.World),
        Scalar("Fixture:Wait", "TimeSpan", "00:00:10", ConfigDaemon.World, "start"),
        Scalar("Fixture:Mode", "FixtureMode", "Fast", ConfigDaemon.World, "live", ModeValues),
        Scalar("Fixture:Path", "string", null, ConfigDaemon.World),
        Scalar("Fixture:Sub:Depth", "int", "3", ConfigDaemon.World),
        new ConfKey("Fixture:Map", "Fixture", "Dictionary<string, int>", ConfKey.Map, "int", null, null,
            [new ConfDefaultEntry("a", "1", false), new ConfDefaultEntry("b", "2", false)], ConfigDaemon.World, "live", "A map.", "name = whole number", "docs/reference/configuration.md#fixture"),
        new ConfKey("Fixture:List", "Fixture", "List<string>", ConfKey.Sequence, "string", null, null,
            [new ConfDefaultEntry("0", "x", true), new ConfDefaultEntry("1", "y", true)], ConfigDaemon.World, "restart", "A list.", "text", "docs/reference/configuration.md#fixture"),
        new ConfKey("Fixture:Items", "Fixture", "List<ItemEntry>", ConfKey.Sequence, "object", null, null,
            [], ConfigDaemon.World, "start", "Entries.", "entries", "docs/reference/configuration.md#fixture"),
        Scalar("Auth:Port", "int", "3724", ConfigDaemon.Realm, "start"),
        Scalar("Database:ConnectionString", "string", string.Empty, ConfigDaemon.Both, "start"),
        Scalar("Startup:Strict", "bool", "false", ConfigDaemon.Both, "start"),
        Scalar("Logging:LogLevel:Default", "string", "Information", ConfigDaemon.Both, "start"),
    ]);
}

using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// Configuration of the map feed's action channel (docs/areas/atlas-actions.md): lets a tool on the feed's pipe run GM commands, with a
/// click point and a unit selection attached, as an audited remote invoker. Off by default and independent of the packet tools: the
/// channel never runs <c>console</c>, <c>inject</c>, <c>send</c> or <c>tap</c>. It is refused on the live server (the
/// <c>ARCANECORE_LIVE_SERVER</c> marker) unless <see cref="AllowOnLiveServer"/> is also set. Changing any value needs a restart.
/// </summary>
public sealed class MapFeedActionsOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "World:Control:MapFeed:Actions";

    /// <summary>The most targets (guids) one action may name.</summary>
    public const int MaxTargets = 50;

    /// <summary>Accept action requests on the map feed (World:Control:MapFeed:Enabled must be on too). Default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// A second, deliberate opt-in: let <see cref="Enabled"/> take effect on the live server (<c>ARCANECORE_LIVE_SERVER=1</c>, set by the
    /// live launcher, which runs the server as Development). The daemon then logs a warning at start. Default off.
    /// </summary>
    public bool AllowOnLiveServer { get; set; }

    /// <summary>
    /// The security the local actor (whoever controls the daemon's Windows account on this machine; the world sees "the daemon account")
    /// runs commands at. Player to Administrator: there is no Console level, so the local actor is never the true console. Default Administrator.
    /// </summary>
    public AccountSecurity LocalSecurity { get; set; } = AccountSecurity.Administrator;

    /// <summary>
    /// Accept web actors (tickets issued by the public Atlas listener's login). Needs the true-console predicate
    /// (<c>CommandContextConsole</c>, design 1.7) in this build: without it the channel logs a warning and refuses web actors. Default off.
    /// </summary>
    public bool WebLogin { get; set; }

    /// <summary>The lowest account security a web login is accepted for. Default Moderator.</summary>
    public AccountSecurity WebLoginMinSecurity { get; set; } = AccountSecurity.Moderator;

    /// <summary>
    /// The account id the local actor counts as for "my own character" checks (<c>.atlas tele</c> of a player at GameMaster level). Set here, in
    /// the world's configuration, never by a tool. Default none: the local actor has no account.
    /// </summary>
    public int? LocalAccountId { get; set; }

    /// <summary>Days the durable audit file's rotated copies are kept. 1 to 3650. Default 90.</summary>
    public int WebAuditDays { get; set; } = 90;

    /// <summary>Also append local actions to the durable audit file (<c>atlas-web-audit.jsonl</c>). Web actions are always recorded. Default on.</summary>
    public bool AuditLocalDurable { get; set; } = true;

    /// <summary>The directory of the durable audit file; null: <c>logs</c> beside the working directory (where the server log goes).</summary>
    public string? AuditDirectory { get; set; }

    /// <summary>Seconds an action waits for its command to settle (a command that holds itself open) before the answer goes out. 1 to 600. Default 30.</summary>
    public int SettleSeconds { get; set; } = 30;

    /// <summary>A template applied to more targets than this needs a confirm code. 0 to 50. Default 1.</summary>
    public int BulkConfirmAbove { get; set; } = 1;

    /// <summary>Local actor: actions per second. Default 10.</summary>
    public int LocalPerSecond { get; set; } = 10;

    /// <summary>Local actor: burst. Default 20.</summary>
    public int LocalBurst { get; set; } = 20;

    /// <summary>Each web actor: actions per second. Default 2.</summary>
    public int WebPerSecond { get; set; } = 2;

    /// <summary>Each web actor: burst. Default 5.</summary>
    public int WebBurst { get; set; } = 5;

    /// <summary>The highest security a web actor runs at, whatever its account has. Default GameMaster.</summary>
    public AccountSecurity PublicMaxSecurity { get; set; } = AccountSecurity.GameMaster;

    /// <summary>
    /// Command paths no web actor may run, matched on the path the command table resolved (so an abbreviation cannot dodge a rule): a
    /// path is denied when it starts with an entry's words ("ban" denies "ban account", "botctl raw" denies "botctl raw send"). The default
    /// list is the account, lifecycle, reload, hot-code, raw bot and moderation commands.
    /// </summary>
    public List<string> PublicDeny { get; set; } =
    [
        "account", "server shutdown", "server restart", "server idleshutdown", "server idlerestart", "reload", "hotcode", "hotmodule",
        "botctl raw", "ban", "unban",
    ];

    /// <summary>Let web actors run <c>.ban</c> and <c>.unban</c>: removes them from <see cref="PublicDeny"/>. Default off.</summary>
    public bool PublicAllowModeration { get; set; }

    /// <summary>Command paths no actor may run, local included (same matching as <see cref="PublicDeny"/>). Default none.</summary>
    public List<string> Deny { get; set; } = [];

    /// <summary>Everything wrong with the values (empty when valid). Pure; any thread.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!Enum.IsDefined(LocalSecurity))
        {
            problems.Add($"LocalSecurity is {(int)LocalSecurity}; it must be Player, Moderator, GameMaster or Administrator");
        }

        if (!Enum.IsDefined(PublicMaxSecurity))
        {
            problems.Add($"PublicMaxSecurity is {(int)PublicMaxSecurity}; it must be Player, Moderator, GameMaster or Administrator");
        }

        if (!Enum.IsDefined(WebLoginMinSecurity))
        {
            problems.Add($"WebLoginMinSecurity is {(int)WebLoginMinSecurity}; it must be Player, Moderator, GameMaster or Administrator");
        }

        Range(problems, nameof(SettleSeconds), SettleSeconds, 1, 600);
        Range(problems, nameof(BulkConfirmAbove), BulkConfirmAbove, 0, MaxTargets);
        Range(problems, nameof(WebAuditDays), WebAuditDays, 1, 3650);
        Range(problems, nameof(LocalPerSecond), LocalPerSecond, 1, 1000);
        Range(problems, nameof(LocalBurst), LocalBurst, 1, 10_000);
        Range(problems, nameof(WebPerSecond), WebPerSecond, 1, 1000);
        Range(problems, nameof(WebBurst), WebBurst, 1, 10_000);
        return problems;
    }

    /// <summary>The words of a deny entry, lower-cased; empty entries are skipped.</summary>
    internal static string[][] DenyWords(IEnumerable<string> entries)
        => [.. entries.Select(e => (e ?? string.Empty).Trim().TrimStart('.', '!').ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(words => words.Length > 0)];

    private static void Range(List<string> problems, string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            problems.Add($"{name} is {value}; it must be {min} to {max}");
        }
    }
}

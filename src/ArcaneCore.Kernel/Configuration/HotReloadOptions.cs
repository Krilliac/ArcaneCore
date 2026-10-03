namespace ArcaneCore.Kernel.Configuration;

/// <summary>
/// The <c>HotReload</c> configuration section. Live reload is operator-initiated only (the
/// <c>.reload</c> chat commands, like vmangos' <c>reload</c> command tree); nothing reloads by
/// itself, and every default below keeps the process behaving exactly as it did without this
/// feature.
/// </summary>
public sealed class HotReloadOptions
{
    public const string SectionName = "HotReload";

    /// <summary>
    /// The one switch for live reload. Default false: a production server does not carry the
    /// reload machinery (vmangos registers the <c>reload</c> root, Chat.cpp:1212, but ArcaneCore's
    /// operator-initiated hot reload is a development tool, so it is opt-in). When false the
    /// reload coordinator is not built, no reloadable is registered and the <c>.reload</c> root does
    /// not exist (the chat reply is "There is no such command"). A development server turns it on
    /// with <c>HotReload:Commands=true</c>. Read once when the world starts.
    /// </summary>
    public bool Commands { get; set; }

    /// <summary>How long a candidate may take to build (database read plus indexing) before the reload is abandoned. 0 = no limit.</summary>
    public int BuildTimeoutMs { get; set; } = 60_000;

    /// <summary>
    /// How long to wait for the world thread to run the swap before abandoning the reload. A swap
    /// that has not started by then is cancelled and never runs later. 0 = no limit.
    /// </summary>
    public int CommitTimeoutMs { get; set; } = 30_000;

    /// <summary>What a reload does when item_template, game_tele or areatrigger_teleport comes back empty. Default Retail: vmangos clears the loaded rows first (ObjectMgr.cpp:3817, 10468, 7708), so the table ends up empty.</summary>
    public EmptyTablePolicy EmptyTables { get; set; } = EmptyTablePolicy.Retail;

    /// <summary>What `.reload config` does with a negative interval or range. Default Retail: vmangos logs an error and uses the default (World.cpp:2949-2977 setConfigPos/setConfigMin).</summary>
    public InvalidNumberPolicy NegativeNumbers { get; set; } = InvalidNumberPolicy.Retail;
}

/// <summary>Empty source table handling for the reloads vmangos clears first.</summary>
public enum EmptyTablePolicy
{
    /// <summary>Retail: the loaded rows are cleared before the result is looked at, so an empty table empties the content.</summary>
    Retail = 0,

    /// <summary>Opt-in safety: an empty table keeps the loaded rows (the early-out vmangos' spell and creature loaders have).</summary>
    KeepLoaded = 1,
}

/// <summary>Negative numeric config handling in <c>.reload config</c>.</summary>
public enum InvalidNumberPolicy
{
    /// <summary>Retail: the key falls back to its default and the reload goes on (vmangos setConfigPos, World.cpp:2949-2977).</summary>
    Retail = 0,

    /// <summary>Opt-in strictness: the whole reload is rejected naming the key.</summary>
    Reject = 1,
}

using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Rates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.ServerConfig;

/// <summary>
/// <c>.server config list|get|set|reset|diff|sources|dump</c>: the one editor of the server options (<see cref="OptionCatalog"/>). A
/// GameMaster may list, get and diff; an Administrator may set, reset, dump and list the sources, because one set changes the game for
/// everyone at once. Restart-only keys are shown but read-only. A set lasts until <c>.reload config</c> or a restart (until a restart for
/// the PacketHeuristics and pack keys, which the reload does not touch) and is never written to a configuration file (to keep a value, edit the file). Every run is in the GM command audit (<c>CommandTable.Execute</c>) and every set
/// and reset is logged with old and new value, actor and reason. A set and a reset change global state, so they are queued with
/// <c>World.Post</c> (serial, at the start of the next tick) and reply from inside it. <c>.rates set</c>, <c>.anticheat set</c>,
/// <c>.movement set</c> and <c>.packetcheck on|off|bots|staff</c> stay and run the same set (<see cref="PostSet"/>), so the old and the new
/// command cannot diverge and <c>diff</c> sees what they change.
/// </summary>
public sealed class ServerConfigCommands : ICommandExtension
{
    /// <summary>Rows per page of a listing (<c>InspectCommands.MaxRows</c>).</summary>
    public const int PageSize = 25;

    public const string ListSyntax = "Syntax: .server config list [$filter] [page #n]\nList the server options (value, apply class: live, next-load or restart, and whether read-only or overridden). "
        + "$filter: part of a path, alias or description (maps, xp, anticheat...). 25 rows per page.";

    public const string GetSyntax = "Syntax: .server config get $key\nShow one option: value, type and bounds, apply class, aliases and, when overridden, its baseline and who changed it. $key: a path (World:Maps:GridUnload), a rate name or an alias.";

    public const string SetSyntax = "Syntax: .server config set $key $value [$reason]\nChange a live or next-load option for everyone until .reload config or restart (PacketHeuristics and pack options: until restart; not saved to any file). "
        + "Restart-only keys are read-only. $value is checked against the key's type and bounds; quote a text value that has spaces. $reason is logged with the change.";

    public const string ResetSyntax = "Syntax: .server config reset [$key|all]\nPut one option (or all overridden ones, the default) back to the value it had before the first change.";

    public const string DiffSyntax = "Syntax: .server config diff\nList the options changed since start or the last .reload config (PacketHeuristics and pack options: since start): baseline, value now, who and why.";

    public const string SourcesSyntax = "Syntax: .server config sources [$filter] [page #n]\nList the configuration providers (last one wins) and, for each option matching $filter, which provider supplies its value, or whether it is a built-in default or overridden.";

    public const string DumpSyntax = "Syntax: .server config dump $section [page #n]\nPrint the effective value of every option whose path starts with $section (World:Maps, Rate, AntiCheat...), secrets masked.";

    public string Path => "server";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("config", AccountSecurity.GameMaster, "Server options. Syntax: .server config [list|get|set|reset|diff|sources|dump]", List, Children:
        [
            new ChatCommand("list", AccountSecurity.GameMaster, ListSyntax, List),
            new ChatCommand("get", AccountSecurity.GameMaster, GetSyntax, Get),
            new ChatCommand("set", AccountSecurity.Administrator, SetSyntax, Set, RetailLevel: 6, IsWrite: true),
            new ChatCommand("reset", AccountSecurity.Administrator, ResetSyntax, Reset, RetailLevel: 6, IsWrite: true),
            new ChatCommand("diff", AccountSecurity.GameMaster, DiffSyntax, Diff),
            new ChatCommand("sources", AccountSecurity.Administrator, SourcesSyntax, Sources, RetailLevel: 6),
            new ChatCommand("dump", AccountSecurity.Administrator, DumpSyntax, Dump, RetailLevel: 6),
        ]),
    ];

    internal static OptionScope ScopeOf(CommandContext context) => new(context.Session.Services, context.World, context.Session.Logger);

    /// <summary>What <see cref="PostSet"/> hands back: the key (null when the name matches none), the result, and the texts of <see cref="OptionCatalog.TrySet"/>.</summary>
    internal readonly record struct SetOutcome(OptionKey? Key, OptionSetResult Result, string Error, string Before, string After);

    /// <summary>
    /// Sets the option <paramref name="name"/> (a path or alias) to the typed <paramref name="value"/> in <c>World.Post</c>, where the catalog is
    /// built for what runs then, and hands the outcome to <paramref name="done"/>, which replies. This is the one set path: <c>.server config set</c>
    /// and the old <c>set</c> commands call it. A name that matches no key gives a null <see cref="SetOutcome.Key"/> and <see cref="OptionSetResult.NotAvailable"/>.
    /// </summary>
    internal static void PostSet(CommandContext context, string name, string value, string? reason, Action<SetOutcome> done)
    {
        OptionScope scope = ScopeOf(context);
        var actor = new OptionActor(context.InvokerName, reason);
        IDisposable hold = context.Defer(); // a console or remote invoker waits for the reply before it reports the command done
        context.World.Post(() =>
        {
            using (hold)
            {
                var catalog = OptionCatalog.Create(scope);
                if (catalog.Find(name) is not { } key)
                {
                    done(new SetOutcome(null, OptionSetResult.NotAvailable, $"{name} is not available: the feature that owns it is not running", string.Empty, string.Empty));
                    return;
                }

                OptionSetResult result = catalog.TrySet(scope, key, value, actor, out string error, out string before, out string after);
                done(new SetOutcome(key, result, error, before, after));
            }
        });
    }

    /// <summary>Splits a trailing <c>page #n</c> off <paramref name="text"/>; false when the number is not a positive integer.</summary>
    internal static bool TryPage(ref string text, out int page)
    {
        page = 1;
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && words[^2].Equals("page", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(words[^1].TrimStart('#'), out page) || page < 1)
            {
                return false;
            }

            text = string.Join(' ', words[..^2]);
        }

        return true;
    }

    private static string Flags(OptionStatus status)
    {
        OptionKey key = status.Key;
        string flags = OptionKey.Show(key.Apply) + (key.Writable ? string.Empty : ", read-only");
        return status.Overridden ? flags + ", OVERRIDDEN" : flags;
    }

    private static string Row(OptionStatus status)
        => status.Available
            ? $"{status.Key.Path} = {status.Key.Format(status.Current)} [{Flags(status)}]"
            : $"{status.Key.Path}: not available (its feature is not running)";

    private static bool Page(CommandContext context, string title, IReadOnlyList<string> rows, int page)
    {
        int pages = Math.Max(1, (rows.Count + PageSize - 1) / PageSize);
        page = Math.Min(page, pages);
        context.Reply($"{title} (page {page}/{pages}; page #n for more):\n" + string.Join('\n', rows.Skip((page - 1) * PageSize).Take(PageSize)));
        return true;
    }

    private static bool List(CommandContext context, string text)
    {
        string filter = text.Trim();
        if (filter.StartsWith("list", StringComparison.OrdinalIgnoreCase) && (filter.Length == 4 || char.IsWhiteSpace(filter[4])))
        {
            filter = filter[4..].Trim();
        }

        if (!TryPage(ref filter, out int page))
        {
            return false;
        }

        OptionScope scope = ScopeOf(context);
        OptionCatalog catalog = OptionCatalog.Create(scope);
        OptionStatus[] matches = [.. catalog.Match(filter).Select(k => catalog.Describe(scope, k))];
        if (matches.Length == 0)
        {
            context.Reply($".server config: no option matches '{filter}'.");
            return true;
        }

        string title = $"Server options ({matches.Length} of {catalog.Keys.Count}, {matches.Count(m => m.Overridden)} overridden; .server config get $key for detail)";
        return Page(context, title, [.. matches.Select(Row)], page);
    }

    private static bool Get(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? name = args.ExtractLiteral();
        if (name is null || !args.IsEmpty)
        {
            return false;
        }

        OptionScope scope = ScopeOf(context);
        OptionCatalog catalog = OptionCatalog.Create(scope);
        if (catalog.Find(name) is not { } key)
        {
            context.Reply($".server config get: unknown option '{name}'. .server config list shows the names.");
            return true;
        }

        OptionStatus status = catalog.Describe(scope, key);
        var lines = new List<string> { Row(status), key.Description };
        string bounds = key.BoundsText.Length == 0 ? string.Empty : $", {key.BoundsText}";
        lines.Add($"type {key.Type}{bounds}; apply {OptionKey.Show(key.Apply)}; {(key.Writable ? "writable" : "read-only")}; owner {key.Group}");
        if (status.Overridden)
        {
            string by = status.Actor is null ? string.Empty : $", set by {status.Actor}{(string.IsNullOrWhiteSpace(status.Reason) ? string.Empty : $" ({status.Reason})")}";
            lines.Add($"OVERRIDDEN: baseline {key.Format(status.Baseline)}{by}");
        }

        if (key.Aliases.Count > 0)
        {
            lines.Add("aliases: " + string.Join(", ", key.Aliases));
        }

        context.Reply(string.Join('\n', lines));
        return true;
    }

    private static bool Set(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? name = args.ExtractLiteral();
        string? value = args.ExtractQuotedOrLiteral();
        if (name is null || value is null)
        {
            return false;
        }

        string? reason = args.IsEmpty ? null : args.Rest.Trim();
        PostSet(context, name, value, reason, outcome =>
        {
            if (outcome.Key is not { } key)
            {
                context.Reply($".server config set: unknown option '{name}'. .server config list shows the names.");
            }
            else if (outcome.Result != OptionSetResult.Ok)
            {
                context.Reply($".server config set: {outcome.Error}.");
            }
            else
            {
                bool untilReload = key.Tracking is not null || OptionOverrides.EndsAtReload(key.Path);
                string lasts = untilReload ? "until .reload config or restart" : "until restart";
                DebugWrite.Record(context, "server option", key.Path, outcome.Before, outcome.After, untilReload ? DebugWriteScope.UntilReload : DebugWriteScope.Live, reason);
                context.Reply($"Option {key.Path}: {outcome.Before} -> {outcome.After} ({lasts}; not saved to any file)."
                    + (key.Apply == OptionApplyClass.NextLoad ? " Applies to what is created or loaded afterwards." : string.Empty));
            }
        });
        return true;
    }

    private static bool Reset(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? name = args.ExtractLiteral();
        if (!args.IsEmpty)
        {
            return false;
        }

        // Like a set, a reset changes global state: it runs in World.Post and replies from inside it.
        OptionScope scope = ScopeOf(context);
        string actor = context.InvokerName;
        IDisposable hold = context.Defer();
        context.World.Post(() =>
        {
            using (hold)
            {
                OptionCatalog catalog = OptionCatalog.Create(scope);
                if (name is null || name.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    // A restore a rate refuses (it would cross its Min/Max pair) waits for another pass; a pass with no progress forces the rest.
                    int count = 0;
                    for (bool force = false; ;)
                    {
                        int before = count;
                        foreach (OptionStatus status in catalog.Diff(scope))
                        {
                            if (ResetAndRecord(context, scope, catalog, status.Key, actor, force, out _) is not null)
                            {
                                count++;
                            }
                        }

                        if (count == before)
                        {
                            if (force)
                            {
                                break;
                            }

                            force = true;
                        }
                        else
                        {
                            force = false;
                        }
                    }

                    context.Reply(count == 0 ? "No option is overridden." : $"{count} option(s) back to their baseline values.");
                }
                else if (catalog.Find(name) is not { } key)
                {
                    context.Reply($".server config reset: unknown option '{name}'. .server config list shows the names.");
                }
                else if (ResetAndRecord(context, scope, catalog, key, actor, force: false, out string? refusal) is not { } now)
                {
                    context.Reply(refusal ?? $"{key.Path} is not overridden.");
                }
                else
                {
                    context.Reply($"{key.Path} is back to its baseline value {now}.");
                }
            }
        });
        return true;
    }

    /// <summary>Resets <paramref name="key"/> and records the write (plan 1.5); returns the shown value now, or null when nothing was reset. World thread.</summary>
    private static string? ResetAndRecord(CommandContext context, OptionScope scope, OptionCatalog catalog, OptionKey key, string actor, bool force, out string? refusal)
    {
        string before = key.Format(catalog.Describe(scope, key).Current);
        if (!catalog.Reset(scope, key, actor, force, out refusal))
        {
            return null;
        }

        string after = key.Format(catalog.Describe(scope, key).Current);
        bool untilReload = key.Tracking is not null || OptionOverrides.EndsAtReload(key.Path);
        DebugWrite.Record(context, "server option", key.Path, before, after, untilReload ? DebugWriteScope.UntilReload : DebugWriteScope.Live, "reset");
        return after;
    }

    private static bool Diff(CommandContext context, string text)
    {
        if (text.Trim().Length != 0)
        {
            return false;
        }

        OptionScope scope = ScopeOf(context);
        OptionCatalog catalog = OptionCatalog.Create(scope);
        IReadOnlyList<OptionStatus> changed = catalog.Diff(scope);
        if (changed.Count == 0)
        {
            context.Reply("No option is overridden; every value is the configured one.");
            return true;
        }

        var rows = changed.Select(s => $"{s.Key.Path}: {s.Key.Format(s.Baseline)} -> {s.Key.Format(s.Current)} [{OptionKey.Show(s.Key.Apply)}]"
            + (s.Actor is null ? string.Empty : $" by {s.Actor}{(string.IsNullOrWhiteSpace(s.Reason) ? string.Empty : $" ({s.Reason})")}")).ToList();
        context.Reply($"{changed.Count} overridden option(s) (baseline -> now; .server config reset undoes):\n" + string.Join('\n', rows));
        return true;
    }

    private static bool Sources(CommandContext context, string text)
    {
        string filter = text.Trim();
        if (!TryPage(ref filter, out int page))
        {
            return false;
        }

        OptionScope scope = ScopeOf(context);
        OptionCatalog catalog = OptionCatalog.Create(scope);
        IConfigurationRoot? root = context.Session.Services.GetService<IConfiguration>() as IConfigurationRoot;
        var rows = new List<string>();
        if (page == 1 && root is not null)
        {
            rows.Add("providers (last wins): " + string.Join(" < ", root.Providers.Select(ConfigSources.Describe)));
        }
        else if (page == 1)
        {
            rows.Add("providers: the configuration was not built from a provider list");
        }

        foreach (OptionKey key in catalog.Match(filter))
        {
            OptionStatus status = catalog.Describe(scope, key);
            string source = status.Overridden
                ? "overridden at runtime"
                : key.ConfigPath.Length == 0 ? "no configuration key known"
                : ConfigSources.Locate(root, key.ConfigPath) is { } provider ? ConfigSources.Describe(provider) : "built-in default";
            rows.Add($"{key.Path}: {source}");
        }

        return Page(context, "Option sources", rows, page);
    }

    private static bool Dump(CommandContext context, string text)
    {
        string section = text.Trim();
        if (!TryPage(ref section, out int page) || section.Length == 0)
        {
            return false;
        }

        OptionScope scope = ScopeOf(context);
        OptionCatalog catalog = OptionCatalog.Create(scope);
        OptionStatus[] matches = [.. catalog.Keys.Where(k => k.Path.StartsWith(section, StringComparison.OrdinalIgnoreCase)).Select(k => catalog.Describe(scope, k))];
        if (matches.Length == 0)
        {
            context.Reply($".server config dump: no option path starts with '{section}'.");
            return true;
        }

        return Page(context, $"Effective values under {section} ({matches.Length}; secrets masked)", [.. matches.Select(m => $"{m.Key.Path} = {m.Key.Format(m.Current)}")], page);
    }
}

/// <summary>Which configuration provider supplies a path, described without host paths.</summary>
internal static class ConfigSources
{
    internal static IConfigurationProvider? Locate(IConfigurationRoot? root, string path)
    {
        if (root is null)
        {
            return null;
        }

        foreach (IConfigurationProvider provider in root.Providers.Reverse())
        {
            if (provider.TryGet(path, out _))
            {
                return provider;
            }
        }

        return null;
    }

    internal static string Describe(IConfigurationProvider provider)
    {
        switch (provider)
        {
            case ArcaneCore.Kernel.Configuration.Conf.ConfConfigurationProvider conf:
                return "conf file " + System.IO.Path.GetFileName(conf.Path);
            case FileConfigurationProvider file:
                return "file " + System.IO.Path.GetFileName(file.Source.Path ?? string.Empty);
        }

        string name = provider.GetType().Name;
        const string suffix = "ConfigurationProvider";
        return name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : name;
    }
}

/// <summary>
/// <c>.rates history [#n]</c> and <c>.rates overrides</c> (read-only, GameMaster): the last rate changes with who and why, and the
/// <c>.rates set</c> lines that would recreate what is overridden now. Added under the <c>.rates</c> root of <see cref="RateCommands"/>.
/// </summary>
public sealed class RatesHistoryCommands : ICommandExtension
{
    public const string HistorySyntax = "Syntax: .rates history [#n]\nThe last n rate changes (set, reset, reload), newest first, with who and why (25 by default, at most 25; the server keeps the last 100).";

    public const string OverridesSyntax = "Syntax: .rates overrides\nThe .rates set lines that recreate every overridden rate, with the configured value each one would return to.";

    public string Path => "rates";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("history", AccountSecurity.GameMaster, HistorySyntax, History),
        new ChatCommand("overrides", AccountSecurity.GameMaster, OverridesSyntax, Overrides),
    ];

    private static bool History(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        int count = ServerConfigCommands.PageSize;
        if (!args.IsEmpty && (!args.ExtractInt32(out count) || count < 1 || !args.IsEmpty))
        {
            return false;
        }

        count = Math.Min(count, ServerConfigCommands.PageSize);
        IReadOnlyList<RateHistoryEntry> history = ServerRates.For(context.World).History;
        if (history.Count == 0)
        {
            context.Reply("No rate has changed since start.");
            return true;
        }

        IEnumerable<string> rows = history.Reverse().Take(count).Select(h =>
            $"{h.At:yyyy-MM-dd HH:mm:ss}Z {h.Change.Kind} {h.Change.Rate.Name}: {ServerRates.Show(h.Change.Old)} -> {ServerRates.Show(h.Change.New)} by {h.Change.Actor}"
            + (string.IsNullOrWhiteSpace(h.Change.Reason) ? string.Empty : $" ({h.Change.Reason})"));
        context.Reply($"Rate changes (newest first, {Math.Min(count, history.Count)} of {history.Count}):\n" + string.Join('\n', rows));
        return true;
    }

    private static bool Overrides(CommandContext context, string text)
    {
        if (text.Trim().Length != 0)
        {
            return false;
        }

        RateScope scope = new(context.Session.Services, context.World, context.Session.Logger);
        ServerRates rates = ServerRates.For(context.World);
        var rows = new List<string>();
        foreach (RateDefinition rate in ServerRates.All)
        {
            RateStatus status = rates.Describe(scope, rate);
            if (status.Available && status.Overridden)
            {
                rows.Add($".rates set {rate.Name} {ServerRates.Show(status.Current)}   (configured {ServerRates.Show(status.Configured)})");
            }
        }

        context.Reply(rows.Count == 0 ? "No rate is overridden." : $"{rows.Count} overridden rate(s):\n" + string.Join('\n', rows));
        return true;
    }
}

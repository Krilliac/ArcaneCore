using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.World.Commands;
using ArcaneCore.World.HotCode;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

/// <summary>
/// The <c>.reload</c> command tree (vmangos <c>reloadCommandTable</c>, Chat.cpp:794-935, root at
/// :1212). Names follow vmangos where a counterpart exists (<c>config</c>, <c>spell_template</c>, <c>all</c>);
/// <c>status</c> is ArcaneCore's addition. A name is any registered <see cref="Game.Reload.IContentReloadable"/>,
/// matched exactly or by unique prefix, so features add their own without touching this file.
/// <para>
/// Security: vmangos splits the root (<c>SEC_DEVELOPER</c>, Chat.cpp:1212) from <c>config</c> and
/// <c>all</c> (<c>SEC_ADMINISTRATOR</c>, Chat.cpp:796,808) on its 0-6 scale; ArcaneCore's scale ends at
/// Administrator (<see cref="AccountSecurity"/>), where the two collapse into one level, as in
/// cmangos-classic's single <c>SEC_ADMINISTRATOR</c> root (Chat.cpp:942).
/// </para>
/// </summary>
public sealed class ReloadCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand(
            "reload",
            AccountSecurity.Administrator,
            "Syntax: .reload #name [reason] | all [reason] | status — reload configuration or content without a restart (config, spell_template, …). The optional free-text reason is announced to staff with the result (World:Announce:HotSwap).",
            Reload,
            [
                new ChatCommand("all", AccountSecurity.Administrator, "Syntax: .reload all [reason] — reload every content table (not the config).", ReloadAll),
                new ChatCommand("status", AccountSecurity.Administrator, "Syntax: .reload status — what can be reloaded and how each reload last ended.", Status),
            ]),
    ];

    private static ReloadFeature Feature(CommandContext context) => context.Session.Services.GetRequiredService<ReloadFeature>();

    /// <summary>The <c>.reload</c> root exists only when <c>HotReload:Commands</c> is on (default off).</summary>
    public bool IsEnabled(IServiceProvider? services) => services is not null && ReloadPolicy.Resolve(services).Commands;

    private static bool Reload(CommandContext context, string args)
    {
        ReloadFeature feature = Feature(context);
        (string word, string reason) = AnnounceTexts.SplitReason(args);
        if (word.Length == 0)
        {
            return false;
        }

        IReadOnlyList<string> names = feature.Coordinator.Names;
        string? name = names.FirstOrDefault(n => n.Equals(word, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            string[] matches = [.. names.Where(n => n.StartsWith(word, StringComparison.OrdinalIgnoreCase))];
            if (matches.Length > 1)
            {
                context.Reply($"Ambiguous reloadable '{word}': {string.Join(", ", matches)}.");
                return true;
            }
            name = matches.Length == 1 ? matches[0] : null;
        }

        if (name is null)
        {
            context.Reply($"There is no reloadable '{word}'. Available: {string.Join(", ", names)}.");
            return true;
        }

        // vmangos HandleReloadCreatureTemplatesCommand / HandleReloadGameObjectTemplatesCommand (ServerCommands.cpp:1758-1785) take an optional entry and
        // reload only that template. The contract here has no entry-aware build, so the whole table is reloaded (a superset); the entry is not a reason.
        string scope = string.Empty;
        if (name is "creature_template" or "gameobject_template" && TakeEntry(ref reason, out uint entry))
        {
            scope = $" (entry {entry}: the whole table is reloaded, a superset of vmangos' single-template reload)";
        }

        context.Reply($"Re-loading {name}{scope}...");
        HotSwapOrigin origin = HotSwapOrigin.FromCommand(context, reason);
        Run(context, async () => [await feature.Coordinator.ReloadAsync(name, origin: origin).ConfigureAwait(false)]);
        return true;
    }

    /// <summary>Whether <paramref name="reason"/> starts with a decimal entry; when it does the entry is removed from the reason.</summary>
    internal static bool TakeEntry(ref string reason, out uint entry)
    {
        int space = reason.IndexOfAny([' ', '\t']);
        string head = space < 0 ? reason : reason[..space];
        if (!uint.TryParse(head, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out entry))
        {
            return false;
        }

        reason = space < 0 ? string.Empty : reason[(space + 1)..].TrimStart();
        return true;
    }

    private static bool ReloadAll(CommandContext context, string args)
    {
        ReloadFeature feature = Feature(context);
        context.Reply("Re-loading all...");
        HotSwapOrigin origin = HotSwapOrigin.FromCommand(context, AnnounceTexts.Reason(args));
        Run(context, () => feature.Coordinator.ReloadAllAsync(origin: origin));
        return true;
    }

    private static bool Status(CommandContext context, string args)
    {
        ReloadFeature feature = Feature(context);
        foreach (string name in feature.Coordinator.Names)
        {
            context.Reply(feature.Coordinator.LastResults.TryGetValue(name, out ReloadResult? last)
                ? $"{name}: {last.Status} {last.At:HH:mm:ss}Z ({last.Elapsed.TotalMilliseconds:0} ms) - {last.Message}"
                : $"{name}: not reloaded since start");
        }

        if (feature.Coordinator.IsBusy)
        {
            context.Reply("A reload is running now.");
        }

        return true;
    }

    /// <summary>
    /// Start the reload without blocking the world thread (the swap needs it), then report on it:
    /// results go back through the world thread, where the invoker's session is written to.
    /// </summary>
    private static void Run(CommandContext context, Func<Task<IReadOnlyList<ReloadResult>>> reload)
    {
        WorldRuntime world = context.World;
        _ = Task.Run(async () =>
        {
            IReadOnlyList<ReloadResult> results;
            try
            {
                results = await reload().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FaultLedger.Shared.Record("reload.commands", ex);
                AnomalyLedger.Shared.Report("reload", "apply-failed", "reload-command", $"error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
                CommandFaultLogging.Log(context, "reload-command", ex, "reload");
                results = [new ReloadResult("reload", ReloadStatus.Failed, ex.Message, [], TimeSpan.Zero, DateTimeOffset.UtcNow)];
            }

            world.Post(() =>
            {
                foreach (ReloadResult result in results)
                {
                    context.Reply(Describe(result));
                    foreach (string note in result.Notes)
                    {
                        context.Reply(note);
                    }
                }
            });
        });
    }

    private static string Describe(ReloadResult result) => result.Status switch
    {
        ReloadStatus.Applied => $"{result.Name} {result.Message}.",
        _ => $"{result.Name} not reloaded ({result.Status}): {result.Message}.",
    };
}

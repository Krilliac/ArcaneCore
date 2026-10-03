using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.World.Commands;
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
            "Syntax: .reload #name | all | status — reload configuration or content without a restart (config, spell_template, …).",
            Reload,
            [
                new ChatCommand("all", AccountSecurity.Administrator, "Syntax: .reload all — reload every content table (not the config).", ReloadAll),
                new ChatCommand("status", AccountSecurity.Administrator, "Syntax: .reload status — what can be reloaded and how each reload last ended.", Status),
            ]),
    ];

    private static ReloadFeature Feature(CommandContext context) => context.Session.Services.GetRequiredService<ReloadFeature>();

    private static bool Disabled(CommandContext context, ReloadFeature feature)
    {
        if (feature.Options.Commands)
        {
            return false;
        }

        context.Reply("Hot reload is disabled (HotReload:Commands).");
        return true;
    }

    private static bool Reload(CommandContext context, string args)
    {
        ReloadFeature feature = Feature(context);
        if (Disabled(context, feature))
        {
            return true;
        }

        string word = args.Trim();
        if (word.Length == 0)
        {
            return false;
        }

        IReadOnlyList<string> names = feature.Coordinator.Names;
        string? name = names.FirstOrDefault(n => n.Equals(word, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            string[] matches = [.. names.Where(n => n.StartsWith(word, StringComparison.OrdinalIgnoreCase))];
            name = matches.Length == 1 ? matches[0] : null;
        }

        if (name is null)
        {
            context.Reply($"There is no reloadable '{word}'. Available: {string.Join(", ", names)}.");
            return true;
        }

        context.Reply($"Re-loading {name}...");
        Run(context, feature, async () => [await feature.Coordinator.ReloadAsync(name).ConfigureAwait(false)]);
        return true;
    }

    private static bool ReloadAll(CommandContext context, string args)
    {
        ReloadFeature feature = Feature(context);
        if (Disabled(context, feature))
        {
            return true;
        }

        context.Reply("Re-loading all...");
        Run(context, feature, () => feature.Coordinator.ReloadAllAsync());
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
    private static void Run(CommandContext context, ReloadFeature feature, Func<Task<IReadOnlyList<ReloadResult>>> reload)
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

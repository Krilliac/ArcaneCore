using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Commands;
using ArcaneCore.World.HotCode;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Features;

/// <summary>
/// <c>.feature list | detach | attach &lt;name&gt;</c>, Administrator only, present only when the module lane is on
/// (<c>World:HotCode:Modules:Enabled</c>, the same switch as <c>.hotmodule</c>). The name selects a running feature; no
/// command takes a path or code. Every request is audited before it runs; the outcome is audited and announced by the
/// <see cref="FeatureHost"/>.
/// </summary>
public static class FeatureCommands
{
    public const string RootName = "feature";

    public static ChatCommand Create(FeatureHost host, HotCodeAudit audit)
        => new(RootName, AccountSecurity.Administrator, "Detach and re-attach world features (opt-in, with the module lane).", Children:
        [
            new ChatCommand("list", AccountSecurity.Administrator, "Syntax: .feature list — the detachable features and their state.",
                (context, args) =>
                {
                    context.Reply(Format(host.List()));
                    return true;
                }),
            Verb("detach", "Syntax: .feature detach <name> [reason] — quiesce the feature, then remove it from the world at a tick boundary (rolled back if it fails).", host, audit, host.DetachAsync),
            Verb("attach", "Syntax: .feature attach <name> [reason] — attach a detached feature again from its handover at a tick boundary.", host, audit, host.AttachAsync),
        ]);

    /// <summary>The reply of <c>.feature list</c>: each detachable feature on its own line, then how many are pinned.</summary>
    public static string Format(IReadOnlyList<FeatureStatus> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        FeatureStatus[] detachable = [.. features.Where(f => f.Kind != DetachKind.Pinned)];
        int pinned = features.Count - detachable.Length;
        if (detachable.Length == 0)
        {
            return $"No detachable features; {pinned} features are pinned.";
        }

        IEnumerable<string> lines = detachable.Select(f =>
            $"{f.Name}: {f.Kind.ToString().ToLowerInvariant()}, {f.State.ToString().ToLowerInvariant()}, {f.Subscriptions} subscriptions"
            + (f.HandoverValues != 0 ? $", handover {f.HandoverValues} values" : string.Empty)
            + (f.ChangedUtc is { } changed ? $", changed {changed:u}" : string.Empty)
            + (f.LastError is { } error ? $", last error: {error}" : string.Empty));
        return $"{string.Join('\n', lines)}\n{pinned} other features are pinned.";
    }

    private static ChatCommand Verb(string verb, string help, FeatureHost host, HotCodeAudit audit, Func<string, HotSwapOrigin?, Task<FeatureResult>> run)
        => new(verb, AccountSecurity.Administrator, help, (context, args) =>
        {
            (string name, string reason) = AnnounceTexts.SplitReason(args);
            if (name.Length == 0)
            {
                return false;
            }

            string who = context.InvokerName;
            audit.Record("feature-command", reason.Length == 0 ? $"{who} {verb} {name}" : $"{who} {verb} {name}; reason: {reason}");
            Task<FeatureResult> task = run(name, HotSwapOrigin.FromCommand(context, reason));
            _ = task.ContinueWith(
                finished => CommandFaultLogging.Log(context, "feature-command", finished.Exception!.GetBaseException(), "feature " + verb),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            // The commit is posted to this very (world) thread: never block on it. Answer from the next tick.
            _ = task.ContinueWith(
                finished => context.World.Post(() => context.Reply(finished.IsCompletedSuccessfully
                    ? $"Feature {verb} {(finished.Result.Ok ? "done" : finished.Result.Result == HotSwapResult.Rejected ? "rejected" : "failed")}: {finished.Result.Detail}"
                    : $"Feature {verb} failed: {finished.Exception?.GetBaseException().Message}")),
                TaskScheduler.Default);
            context.Reply($"Feature {verb} requested.");
            return true;
        });
}

/// <summary>Appends <c>.feature</c> to the live command table once the world runs.</summary>
internal sealed class FeatureCommandHost(
    FeatureHost features,
    CommandTableSource commands,
    HotCodeAudit audit,
    ILogger<FeatureCommandHost> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        CommandAddResult added = commands.TryAdd([FeatureCommands.Create(features, audit)]);
        if (!added.Applied)
        {
            logger.LogError("The .feature command was not registered: {Error}", added.Error);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class FeatureCommandServiceCollectionExtensions
{
    /// <summary>
    /// Register <c>.feature</c> (call only when <c>World:HotCode:Modules:Enabled</c> is set, next to
    /// <c>AddHotModules</c>; nothing is registered otherwise).
    /// </summary>
    public static IServiceCollection AddFeatureCommands(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new HotCodeAudit(sp.GetRequiredService<IOptions<HotCodeOptions>>().Value.AuditLogPath, logger: sp.GetService<ILogger<HotCodeAudit>>()));
        services.AddHostedService<FeatureCommandHost>();
        return services;
    }
}

using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Features;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.HotCode;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Worldport;

/// <summary>
/// <c>.worldport far | readd | status</c>: the Tier 3 same-map world-port spike (<see cref="WorldportSpike"/>,
/// docs/design/hot-reload-tiers.md section 5.5). Administrator only, and the root exists only when <c>World:HotCode:Enabled</c> is
/// set, which the launch gate accepts only in a Development or Staging environment. No argument names a path or code; every request
/// is audited (<see cref="HotCodeAudit"/>, kind <c>worldport-spike</c>) before it runs.
/// </summary>
public static class WorldportSpikeCommands
{
    public const string RootName = "worldport";

    public static ChatCommand Create(WorldportSpike spike, HotCodeAudit? audit)
    {
        ArgumentNullException.ThrowIfNull(spike);
        return new(RootName, AccountSecurity.Administrator, "Same-map world-port experiment (Tier 3 spike; development only, with World:HotCode:Enabled).", Children:
        [
            new ChatCommand("far", AccountSecurity.Administrator,
                "Syntax: .worldport far [#playername] [$x $y $z [$o]] — teleport the named or selected player (or yourself) to the map it is on through the FAR path: SMSG_TRANSFER_PENDING and SMSG_NEW_WORLD with the same map id, then MSG_MOVE_WORLDPORT_ACK. Without coordinates, to where it stands. Every step is logged with its time (worldport-spike).",
                (context, text) => Far(context, text, spike, audit)),
            new ChatCommand("readd", AccountSecurity.Administrator,
                "Syntax: .worldport readd [#playername] — take the named or selected player (or yourself) out of its map and put it back where it stands, with the login packets and its create block again but no SMSG_NEW_WORLD (comparison for .worldport far).",
                (context, text) => ReAdd(context, text, spike, audit)),
            new ChatCommand("status", AccountSecurity.Administrator,
                "Syntax: .worldport status — the open and the last finished spike runs, each step with its time since the request.",
                (context, _) =>
                {
                    IReadOnlyList<WorldportTrace> runs = spike.Recent;
                    context.Reply(runs.Count == 0 ? "No spike runs yet." : string.Join('\n', runs.Select(r => r.Format())));
                    return true;
                }),
        ]);
    }

    private static bool Far(CommandContext context, string text, WorldportSpike spike, HotCodeAudit? audit)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? name = words.Length > 0 && !IsNumber(words[0]) ? words[0] : null;
        string[] numbers = name is null ? words : words[1..];
        if (numbers.Length is not (0 or 3 or 4) || !numbers.All(IsNumber))
        {
            context.Reply("Syntax: .worldport far [#playername] [$x $y $z [$o]]");
            return true;
        }

        if (!GmTargets.TryPlayer(context, name, out Player target) || !context.CanActOn(target))
        {
            return true;
        }

        float[] at = [.. numbers.Select(n => float.Parse(n, NumberStyles.Float, CultureInfo.InvariantCulture))];
        (float x, float y, float z, float o) = at.Length == 0
            ? (target.X, target.Y, target.Z, target.Orientation)
            : (at[0], at[1], at[2], at.Length == 4 ? at[3] : target.Orientation);
        audit?.Record("worldport-spike", string.Create(CultureInfo.InvariantCulture, $"{context.InvokerName} far {target.Name} map {target.MapId} {x} {y} {z} {o}"));
        WorldportTrace? trace = spike.StartFar(target, x, y, z, o, context.InvokerName, out string? refusal);
        context.Reply(trace is null || trace.Finished
            ? "Worldport far not started: " + refusal
            : string.Create(CultureInfo.InvariantCulture, $"Worldport far #{trace.Id} started for {target.Name} on map {trace.MapId}; follow it with .worldport status or the worldport-spike log lines."));
        return true;
    }

    private static bool ReAdd(CommandContext context, string text, WorldportSpike spike, HotCodeAudit? audit)
    {
        string trimmed = text.Trim();
        if (!GmTargets.TryPlayer(context, trimmed.Length == 0 ? null : trimmed, out Player target) || !context.CanActOn(target))
        {
            return true;
        }

        audit?.Record("worldport-spike", string.Create(CultureInfo.InvariantCulture, $"{context.InvokerName} readd {target.Name} map {target.MapId}"));
        WorldportTrace? trace = spike.StartReAdd(target, context.InvokerName, out string? refusal);
        context.Reply(trace is null
            ? "Worldport readd not started: " + refusal
            : string.Create(CultureInfo.InvariantCulture, $"Worldport readd #{trace.Id} started for {target.Name} on map {trace.MapId}; follow it with .worldport status or the worldport-spike log lines."));
        return true;
    }

    private static bool IsNumber(string word)
        => float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value);
}

/// <summary>Appends <c>.worldport</c> to the live command table at start (only registered with <c>World:HotCode:Enabled</c>).</summary>
internal sealed class WorldportSpikeHost(WorldportSpike spike, CommandTableSource commands, IServiceProvider services, ILogger<WorldportSpikeHost> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        CommandAddResult added = commands.TryAdd([WorldportSpikeCommands.Create(spike, services.GetService<HotCodeAudit>())]);
        if (!added.Applied)
        {
            logger.LogError("The .worldport command was not registered: {Error}", added.Error);
        }
        else
        {
            logger.LogWarning("The same-map world-port spike (.worldport, Administrator) is available: World:HotCode:Enabled is set.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Registers the spike only when <c>World:HotCode:Enabled</c> is set (Development or Staging, enforced by the launch gate); with it
/// off nothing is registered and <c>.worldport</c> does not exist. Discovered by <c>AddWorldDaemon</c>.
/// </summary>
public sealed class WorldportSpikeServiceModule : IWorldServiceModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.IsHotCodeEnabled())
        {
            return;
        }

        services.AddSingleton(sp => new WorldportSpike(
            sp.GetRequiredService<WorldRuntime>(),
            sp.GetRequiredService<TeleportFeature>,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WorldportSpike).Namespace!)));
        services.AddHostedService<WorldportSpikeHost>();
    }
}

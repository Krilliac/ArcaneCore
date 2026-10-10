using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Character.Boost;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// Gives a running bot its level and its place, as services and not as chat commands: the level and kit through
/// <see cref="CharacterBoost.Run"/> (the engine of <c>.character boost</c>; a bot is its own invoker, so it sends no GM message) and the
/// place through the teleport service (a bot acknowledges its own far and near teleports every tick, PlayerbotMovementControl).
/// <para>Thread affinity: <see cref="Provision"/> and <see cref="Relocate"/> run on the world thread (callers use <c>world.InvokeAsync</c>).
/// Every refusal is a code and changes nothing further; nothing throws for an expected refusal.</para>
/// </summary>
internal sealed class PopulationProvisioner(IServiceProvider services)
{
    /// <summary>Whether the boost content is loaded; when not, the missing piece is in <paramref name="missing"/> (world thread).</summary>
    public bool BoostAvailable(out string missing) => BoostEnvironment.Resolve(services, out missing) is not null;

    /// <summary>
    /// Boost the bot to <paramref name="level"/> when it is above its own, then teleport it to <paramref name="location"/>. Returns null on
    /// success, otherwise the refusal code (the bot is left as it was at the step that refused).
    /// </summary>
    public string? Provision(Guid botId, int level, WorldSafeLoc location)
    {
        if (services.GetService<ManagedPlayerbotFeature>()?.FindSession(botId) is not { } session || session.Player is not { IsInWorld: true } player)
            return "bot-not-running";
        if (level > player.Level)
        {
            string? refusal = !player.IsAlive ? "bot-dead"
                : player.Combat.IsInCombat ? "bot-in-combat"
                : !player.CanMutateQuestSettlementState ? "bot-settling-quest"
                : null;
            if (refusal is not null) return refusal;
            BoostEnvironment? environment = BoostEnvironment.Resolve(services, out string missing);
            if (environment is null) return "boost-unavailable: " + missing;
            var context = new CommandContext(session, player, services.GetRequiredService<CommandTable>());
            CharacterBoost.Run(context, player, (byte)level, environment);
        }

        return TeleportTo(player, location);
    }

    /// <summary>Teleport an idle bot to a graveyard (no boost). Null on success, otherwise the refusal code.</summary>
    public string? Relocate(Guid botId, WorldSafeLoc location)
    {
        if (services.GetService<ManagedPlayerbotFeature>()?.FindSession(botId) is not { } session || session.Player is not { IsInWorld: true } player)
            return "bot-not-running";
        if (!player.IsAlive) return "bot-dead";
        if (player.Combat.IsInCombat) return "bot-in-combat";
        return TeleportTo(player, location);
    }

    private string? TeleportTo(Player player, WorldSafeLoc location)
    {
        if (services.GetService<TeleportFeature>() is not { } feature) return "teleport-unavailable";
        TeleportService teleports = feature.Teleports;
        if (!teleports.CanTeleportTo(player, location.MapId, location.X, location.Y, location.Z, location.Orientation)) return "teleport-refused";
        return teleports.TeleportTo(player, location.MapId, location.X, location.Y, location.Z, location.Orientation) ? null : "teleport-refused";
    }
}

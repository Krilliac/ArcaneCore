using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>
/// Dungeon movement and death recovery of one bot (<c>dungeon-bot-*</c>), against The Deadmines (map 36, entrance trigger 78) with
/// whatever <c>World:Playerbots:AllowedMaps</c> says — the default continents [0, 1] included: walking inside the instance
/// (<see cref="PlayerbotMapPolicy"/>), crossing the entrance trigger under its own motion (<see cref="PlayerbotAreaTriggers"/>), a
/// death inside recovered through the entrance as a ghost, and a stalled corpse run that ends at the spirit healer
/// (<see cref="PlayerbotRecovery"/>). Each uses its own bot (<see cref="BotName"/>) and brings it back to where it logged in, alive,
/// whatever happened, so a bot is never left saved in the dungeon or dead.
/// </summary>
public static class ScenarioDungeonBots
{
    /// <summary>The bot these scenarios use (created on first use, reused afterwards; not one of the shared pair).</summary>
    public const string BotName = "Scndelver";

    /// <summary>How far the bot walks inside, along the entrance's facing.</summary>
    public const float InsideWalkYards = 15f;

    internal readonly record struct Origin(uint MapId, float X, float Y, float Z, float Orientation);

    /// <summary>The Deadmines map, its entrance trigger and teleport must be there; returns the entrance teleport.</summary>
    internal static Task<AreaTriggerTeleport> RequireContentAsync(ScenarioContext context)
        => context.StepAsync("the Deadmines content is there", () => context.ReadAsync(() =>
        {
            WorldMaps maps = WorldMaps.Of(context.World);
            ScenarioContext.Expect(maps.Registry.Find(DungeonEntryScenario.Deadmines) is { IsDungeon: true },
                $"map {DungeonEntryScenario.Deadmines} is not a known dungeon (map_template)");
            ScenarioContext.Expect(maps.FindAreaTrigger(DungeonEntryScenario.EntranceTrigger) is not null,
                $"no areatrigger_template {DungeonEntryScenario.EntranceTrigger}");
            AreaTriggerTeleport teleport = maps.FindAreaTriggerTeleport(DungeonEntryScenario.EntranceTrigger)
                ?? throw new ScenarioAssertionException($"no areatrigger_teleport {DungeonEntryScenario.EntranceTrigger}");
            ScenarioContext.ExpectEqual(DungeonEntryScenario.Deadmines, teleport.TargetMap, "entrance target map");
            return teleport;
        }));

    /// <summary>Log the bot in, note where it stands, and raise it to the entrance's level.</summary>
    internal static async Task<(ScenarioBot Bot, Origin Origin)> LoginAsync(ScenarioContext context, AreaTriggerTeleport entrance)
    {
        ScenarioBot bot = await context.StepAsync("login " + BotName, () => context.LoginAsync(BotName)).ConfigureAwait(false);
        Origin origin = await bot.ReadAsync(p => new Origin(p.MapId, p.X, p.Y, p.Z, p.Orientation)).ConfigureAwait(false);
        await context.StepAsync($"{BotName} reaches level {entrance.RequiredLevel}", async () =>
            ScenarioContext.Expect(await context.RaiseLevelAsync(bot, entrance.RequiredLevel).ConfigureAwait(false) >= entrance.RequiredLevel,
                $"{BotName} is below {entrance.RequiredLevel}")).ConfigureAwait(false);
        return (bot, origin);
    }

    /// <summary>Hand the bot to <paramref name="controller"/> (null: its own brain, autonomous mode).</summary>
    internal static async Task DriveAsync(ScenarioContext context, ScenarioBot bot, IPlayerbotController? controller, string what)
        => await context.StepAsync(what, async () => ScenarioContext.Expect(
            await context.Services.GetRequiredService<ManagedPlayerbotFeature>().SetControllerAsync(bot.BotId, controller).ConfigureAwait(false),
            $"{BotName} is not running")).ConfigureAwait(false);

    /// <summary>Kill the bot where it stands (the ordinary death path).</summary>
    internal static Task KillAsync(ScenarioContext context, ScenarioBot bot)
        => context.StepAsync($"{BotName} dies", () => context.ReadAsync(() =>
        {
            Player player = bot.RequirePlayer();
            player.Health = 0;
            player.Map!.Combat.KillPlayer(player);
            ScenarioContext.Expect(!player.IsAlive, $"{BotName} did not die");
            return true;
        }));

    /// <summary>The bot is still running: no fault was recorded, it was not kicked or quarantined.</summary>
    internal static Task ExpectNoFaultAsync(ScenarioContext context, ScenarioBot bot)
        => context.StepAsync($"{BotName} has no fault", () => context.ReadAsync(() =>
        {
            ManagedPlayerbotFeature bots = context.Services.GetRequiredService<ManagedPlayerbotFeature>();
            PlayerbotStatus status = bots.Snapshot().FirstOrDefault(s => s.BotId == bot.BotId)
                ?? throw new ScenarioAssertionException($"{BotName} has no status");
            ScenarioContext.Expect(status.State == ManagedPlayerbotState.Running && status.ErrorCode is null,
                $"{BotName} is {status.State} ({status.ErrorCode ?? "no code"})");
            ScenarioContext.Expect(bots.FindSession(bot.BotId) is { State: SessionState.InWorld }, $"{BotName} lost its session");
            return true;
        }));

    /// <summary>
    /// Cleanup: script the bot again, bring it back to life and to where it logged in. Skipped for a bot that is not online (a
    /// fault already kicked it; its saved place is whatever the fault left).
    /// </summary>
    internal static async Task BringBackAsync(ScenarioContext context, Origin origin)
    {
        ManagedPlayerbotFeature bots = context.Services.GetRequiredService<ManagedPlayerbotFeature>();
        PlayerbotStatus? status = bots.Snapshot().FirstOrDefault(s => s.Name.Equals(BotName, StringComparison.OrdinalIgnoreCase));
        if (status is null || bots.FindSession(status.BotId) is not { State: SessionState.InWorld }) return;
        TeleportService teleports = context.Services.GetRequiredService<TeleportFeature>().Teleports;
        await context.CleanupAsync($"bring {BotName} back alive to where it logged in", async () =>
        {
            ScenarioBot bot = await context.LoginAsync(BotName).ConfigureAwait(false);
            await context.WaitUntilAsync($"{BotName} is not between maps", () => bot.Session?.Player is not { } player
                || (player.IsInWorld && teleports.StageOf(player) is null)).ConfigureAwait(false);
            await context.ReadAsync(() =>
            {
                Player player = bot.RequirePlayer();
                if (!player.IsAlive)
                {
                    player.Map!.Combat.ResurrectPlayer(player, 1f, applySickness: false);
                    player.Map!.Combat.SpawnCorpseBones(player);
                }

                return true;
            }).ConfigureAwait(false);
            bool away = await bot.ReadAsync(p => p.MapId != origin.MapId
                || Vector2.Distance(new(p.X, p.Y), new(origin.X, origin.Y)) > 1f).ConfigureAwait(false);
            if (away) await context.PlaceAsync(bot, origin.MapId, origin.X, origin.Y, origin.Z, origin.Orientation).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>A wait that shows in the report as a step of its own.</summary>
    internal static Task WaitStepAsync(ScenarioContext context, string what, Func<bool> condition, TimeSpan timeout)
        => context.StepAsync(what, () => context.WaitUntilAsync(what, condition, timeout));

    /// <summary>Walk the bot in through the entrance trigger the scripted way (CMSG_AREATRIGGER) and wait until it is inside.</summary>
    internal static async Task EnterAsync(ScenarioContext context, ScenarioBot bot)
    {
        await context.TakeAreaTriggerAsync(bot, DungeonEntryScenario.EntranceTrigger, DungeonEntryScenario.Deadmines).ConfigureAwait(false);
        await context.StepAsync($"{BotName} is in an instance of The Deadmines", async () =>
            ScenarioContext.Expect(await bot.ReadAsync(p => p.Map!.InstanceId).ConfigureAwait(false) != 0, "the dungeon map is not an instance"))
            .ConfigureAwait(false);
    }

    /// <summary>Where the walk from the entrance landing goes: <see cref="InsideWalkYards"/> along the landing's facing.</summary>
    internal static Vector3 InsideDestination(AreaTriggerTeleport entrance)
        => new(entrance.TargetX + (MathF.Cos(entrance.TargetOrientation) * InsideWalkYards),
            entrance.TargetY + (MathF.Sin(entrance.TargetOrientation) * InsideWalkYards), entrance.TargetZ);
}

/// <summary>
/// A controller that walks the bot to one point with the brain's own navigation (<see cref="PlayerbotNavigation.TryPlan"/> and
/// <see cref="PlayerbotNavigation.TryAdvance"/>, which the brain's goals call), thinking every 500 ms and acknowledging the
/// server's orders like the brain. It never sends anything else, so an area trigger it crosses is reported by the motion alone.
/// It stops when the bot arrives, or when the bot leaves the map it started on (a trigger took it away).
/// </summary>
internal sealed class ScenarioRouteWalker(PlayerbotOptions options, Vector3 destination) : IPlayerbotController
{
    private const uint ThinkMs = 500;
    private PlayerbotRoute? _route;
    private uint _accumulated;
    private uint? _startMap;

    public bool Done { get; private set; }

    public bool Arrived { get; private set; }

    public int PlanFailures { get; private set; }

    public void Tick(PlayerbotControllerContext context, uint elapsedMs)
    {
        context.Session.DrainManagedPackets();
        if (context.AcknowledgeServerOrders() || Done || context.Player is not { IsInWorld: true } player) return;
        _startMap ??= player.MapId;
        if (player.MapId != _startMap)
        {
            Done = true;
            return;
        }

        _accumulated += elapsedMs;
        if (_accumulated < ThinkMs) return;
        uint interval = _accumulated;
        _accumulated = 0;
        if (Vector2.Distance(new(player.X, player.Y), new(destination.X, destination.Y)) <= 1f)
        {
            PlayerbotMovementControl.Stop(context.Session, player);
            Arrived = Done = true;
            return;
        }

        if (_route is null || _route.Complete)
        {
            if (!PlayerbotNavigation.TryPlan(player, destination, options, out _route))
            {
                _route = null;
                PlanFailures++;
                return;
            }
        }

        if (!PlayerbotNavigation.TryAdvance(context.Session, _route!, options, interval, context.World.NowMs)) _route = null;
    }

    public void Detached(Guid botId)
    {
    }
}

/// <summary>
/// <c>dungeon-bot-walk-inside</c>: a bot that entered The Deadmines plans and walks a route inside it, although the dungeon is not
/// in AllowedMaps: the instance a bot is in is always walkable (<see cref="PlayerbotMapPolicy"/>). Before, TryPlan refused every
/// map outside AllowedMaps and a bot inside a dungeon could not take a step.
/// </summary>
public sealed class DungeonBotWalkInsideScenario : IPlayerbotScenario
{
    public string Name => "dungeon-bot-walk-inside";

    public string Description => "a bot inside The Deadmines plans and walks a route there, with the dungeon outside AllowedMaps";

    public async Task RunAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AreaTriggerTeleport entrance = await ScenarioDungeonBots.RequireContentAsync(context).ConfigureAwait(false);
        (ScenarioBot bot, ScenarioDungeonBots.Origin origin) = await ScenarioDungeonBots.LoginAsync(context, entrance).ConfigureAwait(false);
        try
        {
            await ScenarioDungeonBots.EnterAsync(context, bot).ConfigureAwait(false);
            PlayerbotOptions options = context.Services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new PlayerbotOptions();
            Vector3 destination = ScenarioDungeonBots.InsideDestination(entrance);
            var walker = new ScenarioRouteWalker(options, destination);
            await ScenarioDungeonBots.DriveAsync(context, bot, walker, $"{ScenarioDungeonBots.BotName} walks with its own navigation").ConfigureAwait(false);
            await ScenarioDungeonBots.WaitStepAsync(context, $"{ScenarioDungeonBots.BotName} walks {ScenarioDungeonBots.InsideWalkYards} yards inside",
                () => walker.Done || walker.PlanFailures >= 3, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await context.StepAsync($"{ScenarioDungeonBots.BotName} arrived inside the dungeon", async () =>
            {
                ScenarioContext.Expect(walker.PlanFailures < 3, $"no route inside the dungeon ({walker.PlanFailures} refused plans)");
                ScenarioContext.Expect(walker.Arrived, "the walk did not arrive");
                ScenarioContext.ExpectEqual(DungeonEntryScenario.Deadmines, await bot.ReadAsync(p => p.MapId).ConfigureAwait(false), "map");
            }).ConfigureAwait(false);
            await ScenarioDungeonBots.ExpectNoFaultAsync(context, bot).ConfigureAwait(false);
        }
        finally
        {
            await ScenarioDungeonBots.BringBackAsync(context, origin).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// <c>dungeon-bot-walk-into-entrance</c>: a bot walking with its own navigation across the Deadmines entrance (trigger 78) is
/// teleported in. Nothing scripted sends CMSG_AREATRIGGER: the motion reports the trigger as it enters the volume, like a client
/// (<see cref="PlayerbotAreaTriggers"/>), and the ordinary handler starts the teleport.
/// </summary>
public sealed class DungeonBotWalkIntoEntranceScenario : IPlayerbotScenario
{
    /// <summary>The walk starts and ends this far from the trigger's centre, on either side of it.</summary>
    public const float ApproachYards = 20f;

    public string Name => "dungeon-bot-walk-into-entrance";

    public string Description => "a bot walking across the Deadmines entrance trigger reports it like a client and is teleported in";

    public async Task RunAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AreaTriggerTeleport entrance = await ScenarioDungeonBots.RequireContentAsync(context).ConfigureAwait(false);
        AreaTriggerTemplate trigger = await context.ReadAsync(() => WorldMaps.Of(context.World).FindAreaTrigger(DungeonEntryScenario.EntranceTrigger)!).ConfigureAwait(false);
        (ScenarioBot bot, ScenarioDungeonBots.Origin origin) = await ScenarioDungeonBots.LoginAsync(context, entrance).ConfigureAwait(false);
        try
        {
            // Across the volume along its own orientation (the box's long side for the entrance), from outside to outside.
            Vector2 across = new(MathF.Cos(trigger.BoxOrientation + (MathF.PI / 2)), MathF.Sin(trigger.BoxOrientation + (MathF.PI / 2)));
            Vector3 start = new(trigger.X - (across.X * ApproachYards), trigger.Y - (across.Y * ApproachYards), trigger.Z);
            Vector3 end = new(trigger.X + (across.X * ApproachYards), trigger.Y + (across.Y * ApproachYards), trigger.Z);
            await context.StepAsync($"{ScenarioDungeonBots.BotName} stands {ApproachYards} yards before the entrance", () =>
                context.PlaceAsync(bot, trigger.MapId, start.X, start.Y, start.Z)).ConfigureAwait(false);
            PlayerbotOptions options = context.Services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new PlayerbotOptions();
            var walker = new ScenarioRouteWalker(options, end);
            await ScenarioDungeonBots.DriveAsync(context, bot, walker, $"{ScenarioDungeonBots.BotName} walks across the entrance").ConfigureAwait(false);
            TeleportService teleports = context.Services.GetRequiredService<TeleportFeature>().Teleports;
            await ScenarioDungeonBots.WaitStepAsync(context, $"{ScenarioDungeonBots.BotName} is teleported into map {DungeonEntryScenario.Deadmines}",
                () => bot.Session!.Player is { IsInWorld: true, Map.MapId: DungeonEntryScenario.Deadmines } player && teleports.StageOf(player) is null,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await ScenarioDungeonBots.ExpectNoFaultAsync(context, bot).ConfigureAwait(false);
        }
        finally
        {
            await ScenarioDungeonBots.BringBackAsync(context, origin).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// <c>dungeon-bot-ghost-entrance</c>: a bot killed inside The Deadmines and switched to autonomous mode releases, appears at the
/// graveyard outside, walks into the entrance trigger 78 as a ghost and is resurrected at the entrance inside (vmangos
/// Player.cpp:1953-1966), with no fault recorded. Before, a body on another map left the recovery waiting until it threw
/// "playerbot-recovery-stalled" and the bot was quarantined.
/// </summary>
public sealed class DungeonBotGhostEntranceScenario : IPlayerbotScenario
{
    public string Name => "dungeon-bot-ghost-entrance";

    public string Description => "a bot killed in The Deadmines releases outside, walks back in as a ghost and is resurrected at the entrance";

    public async Task RunAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AreaTriggerTeleport entrance = await ScenarioDungeonBots.RequireContentAsync(context).ConfigureAwait(false);
        (ScenarioBot bot, ScenarioDungeonBots.Origin origin) = await ScenarioDungeonBots.LoginAsync(context, entrance).ConfigureAwait(false);
        try
        {
            await ScenarioDungeonBots.EnterAsync(context, bot).ConfigureAwait(false);
            await ScenarioDungeonBots.KillAsync(context, bot).ConfigureAwait(false);
            await ScenarioDungeonBots.DriveAsync(context, bot, null, $"{ScenarioDungeonBots.BotName} goes autonomous").ConfigureAwait(false);
            TeleportService teleports = context.Services.GetRequiredService<TeleportFeature>().Teleports;
            uint outside = await context.ReadAsync(() => WorldMaps.Of(context.World).FindAreaTrigger(DungeonEntryScenario.EntranceTrigger)!.MapId).ConfigureAwait(false);
            await ScenarioDungeonBots.WaitStepAsync(context, $"{ScenarioDungeonBots.BotName} releases and appears outside as a ghost", () =>
                bot.Session!.Player is { IsInWorld: true } player && (player.Flags & PlayerFlags.Ghost) != 0
                && player.MapId == outside && teleports.StageOf(player) is null, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await ScenarioDungeonBots.WaitStepAsync(context, $"{ScenarioDungeonBots.BotName} walks in as a ghost and is resurrected at the entrance", () =>
                bot.Session!.Player is { IsInWorld: true, IsAlive: true, Map.MapId: DungeonEntryScenario.Deadmines } player
                && teleports.StageOf(player) is null, TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            await context.StepAsync($"{ScenarioDungeonBots.BotName} stands at the entrance, its body gone", async () =>
            {
                (float distance, bool corpse) = await bot.ReadAsync(p =>
                    (Vector2.Distance(new(p.X, p.Y), new(entrance.TargetX, entrance.TargetY)), p.Combat.Corpse is not null)).ConfigureAwait(false);
                ScenarioContext.Expect(distance < 10f, $"{distance:F1} yards from the entrance landing");
                ScenarioContext.Expect(!corpse, "the body is still there");
            }).ConfigureAwait(false);
            await ScenarioDungeonBots.ExpectNoFaultAsync(context, bot).ConfigureAwait(false);
        }
        finally
        {
            await ScenarioDungeonBots.BringBackAsync(context, origin).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// <c>dungeon-bot-spirit-healer</c>: a bot whose corpse run stalls (its body is moved far below the ground, where no route
/// reaches) takes the spirit healer (CMSG_SPIRIT_HEALER_ACTIVATE) instead of faulting, and is not quarantined. It dies outside the
/// Deadmines entrance, where a spirit healer stands near the graveyard. Before, a walk that stopped closing on the body threw
/// "playerbot-recovery-stuck" and the fault quarantined the bot.
/// </summary>
public sealed class DungeonBotSpiritHealerScenario : IPlayerbotScenario
{
    /// <summary>How far below the ground the body is moved.</summary>
    public const float CorpseDepth = 500f;

    public string Name => "dungeon-bot-spirit-healer";

    public string Description => "a bot whose corpse run stalls uses the spirit healer and is not quarantined";

    public async Task RunAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AreaTriggerTeleport entrance = await ScenarioDungeonBots.RequireContentAsync(context).ConfigureAwait(false);
        (ScenarioBot bot, ScenarioDungeonBots.Origin origin) = await ScenarioDungeonBots.LoginAsync(context, entrance).ConfigureAwait(false);
        try
        {
            // Outside the dungeon: where its exit trigger puts a player, a little further from the entrance.
            AreaTriggerTeleport exit = await context.ReadAsync(() => WorldMaps.Of(context.World).FindAreaTriggerTeleport(DungeonEntryScenario.ExitTrigger)
                ?? throw new ScenarioAssertionException($"no areatrigger_teleport {DungeonEntryScenario.ExitTrigger}")).ConfigureAwait(false);
            float x = exit.TargetX + (MathF.Cos(exit.TargetOrientation) * 15f);
            float y = exit.TargetY + (MathF.Sin(exit.TargetOrientation) * 15f);
            await context.StepAsync($"{ScenarioDungeonBots.BotName} stands outside the Deadmines", () =>
                context.PlaceAsync(bot, exit.TargetMap, x, y, exit.TargetZ, exit.TargetOrientation)).ConfigureAwait(false);
            await ScenarioDungeonBots.KillAsync(context, bot).ConfigureAwait(false);
            await ScenarioDungeonBots.DriveAsync(context, bot, null, $"{ScenarioDungeonBots.BotName} goes autonomous").ConfigureAwait(false);
            TeleportService teleports = context.Services.GetRequiredService<TeleportFeature>().Teleports;
            await ScenarioDungeonBots.WaitStepAsync(context, $"{ScenarioDungeonBots.BotName} releases", () => bot.Session!.Player is { IsInWorld: true } player
                && (player.Flags & PlayerFlags.Ghost) != 0 && player.Combat.Corpse is not null && teleports.StageOf(player) is null,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Vector3 body = await context.StepAsync($"its body sinks {CorpseDepth} yards below the ground", () => context.ReadAsync(() =>
            {
                Player player = bot.RequirePlayer();
                var corpse = player.Combat.Corpse ?? throw new ScenarioAssertionException("no body");
                corpse.SetPosition(corpse.X, corpse.Y, corpse.Z - CorpseDepth, corpse.Orientation);
                return new Vector3(corpse.X, corpse.Y, corpse.Z);
            })).ConfigureAwait(false);
            await ScenarioDungeonBots.WaitStepAsync(context, $"{ScenarioDungeonBots.BotName} is resurrected", () =>
                bot.Session!.Player is { IsInWorld: true, IsAlive: true } player && teleports.StageOf(player) is null,
                TimeSpan.FromSeconds(120)).ConfigureAwait(false);
            // The body was out of every reclaim range (and the living cannot see the healers any more): coming back alive with the
            // body gone and far from it is the spirit healer's resurrection.
            await context.StepAsync($"{ScenarioDungeonBots.BotName} came back at the spirit healer, not at its body", () => context.ReadAsync(() =>
            {
                Player player = bot.RequirePlayer();
                ScenarioContext.Expect(player.Combat.Corpse is null, "the body is still there");
                float distance = Vector3.Distance(body, new Vector3(player.X, player.Y, player.Z));
                ScenarioContext.Expect(distance > CorpseDepth / 2, $"{distance:F1} yards from the body");
                return true;
            })).ConfigureAwait(false);
            await ScenarioDungeonBots.ExpectNoFaultAsync(context, bot).ConfigureAwait(false);
        }
        finally
        {
            await ScenarioDungeonBots.BringBackAsync(context, origin).ConfigureAwait(false);
        }
    }
}

using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// A world on the manual clock with flat open ground, the monster faction hostile to the human bot, and a hand-driven brain
/// (<see cref="Think"/>: one think, then the clock moves): time moves only when a test says so. Creatures are spawned into the
/// map's own creature system, so their aggro, assistance, chase and leash are the server's.
/// </summary>
internal sealed class RiskTestWorld : IAsyncDisposable
{
    public const uint MonsterFaction = 14;
    public const uint ThinkMs = 100;

    private RiskTestWorld(WorldTestHost host, WorldSession session, PlayerbotOptions options, CreatureMapSystem creatures)
    {
        Host = host;
        Session = session;
        Options = options;
        Creatures = creatures;
        Brain = new PlayerbotBrain(session, options);
    }

    public WorldTestHost Host { get; }

    public WorldSession Session { get; }

    public PlayerbotOptions Options { get; }

    public CreatureMapSystem Creatures { get; }

    public PlayerbotBrain Brain { get; }

    public Player Player => Session.Player!;

    /// <param name="ai">EventAI rows for the creatures (their templates name <c>EventAI</c>), loaded as the world's creature content.</param>
    public static async Task<RiskTestWorld> StartAsync(Action<PlayerbotOptions>? configure = null, CreatureAiContent? ai = null)
    {
        WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            if (ai is not null) services.AddSingleton<ICreatureDataStore>(new AiStore(ai));
            services.AddSingleton<IWorldFeature, ManualClock>();
            services.AddSingleton(new FactionTemplateCatalog(
            [
                new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),   // human player
                new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),  // monster
            ]));
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var options = new PlayerbotOptions
        {
            Enabled = true, ThinkIntervalMs = (int)ThinkMs, MaxActionsPerTick = 8, MaxPathPoints = 128, MaxRouteYards = 300,
        };
        configure?.Invoke(options);
        CreatureMapSystem creatures = await host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            WorldCollision.Of(host.World).Install(lineOfSight: new FlatGround(player.Z));
            if (player.Map!.FindUpdater<CreatureMapSystem>() is { } existing) return existing;
            var system = new CreatureMapSystem(player.Map, new CreatureContent([], [], [], [], [], ai), random: new Random(7));
            player.Map.AddUpdater(system);
            return system;
        });
        return new RiskTestWorld(host, session, options, creatures);
    }

    public static CreatureTemplate Template(uint entry, byte level = 1, uint health = 60, float minDamage = 2, float maxDamage = 3,
        uint rank = 0, float runSpeed = 1.14286f, uint type = 1) => new()
    {
        Entry = entry, Name = "risk mob " + entry, Faction = MonsterFaction, CreatureType = type, MinLevel = level, MaxLevel = level,
        MinLevelHealth = health, MaxLevelHealth = health, MinMeleeDamage = minDamage, MaxMeleeDamage = maxDamage,
        MeleeBaseAttackTime = 2000, Rank = rank, SpeedRun = runSpeed, DisplayIds = [49],
    };

    /// <summary>Spawn a creature <paramref name="dx"/>, <paramref name="dy"/> yards from where the bot stands (world thread).</summary>
    public Creature Spawn(CreatureTemplate template, float dx, float dy)
    {
        Player player = Player;
        return Creatures.SpawnTemporary(template, player.X + dx, player.Y + dy, player.Z, 0);
    }

    /// <summary>
    /// World thread: <paramref name="creature"/> and the bot fight each other (each on the other's threat list, as after a pull's
    /// first blows), so the creature's AI keeps the bot as its victim instead of evading an empty threat list.
    /// </summary>
    public void Engage(Creature creature)
    {
        Player player = Player;
        if (!player.Map!.Combat.Attack(creature, player)) throw new InvalidOperationException("the creature cannot attack the bot");
        player.Map!.Combat.DealDamage(player, creature, 1);
        player.Map!.Combat.DealDamage(creature, player, 1);
    }

    public Task<T> OnWorldAsync<T>(Func<T> action) => Host.OnWorldAsync(action);

    /// <summary>Spawned creatures become visible to the bot on the next world update.</summary>
    public async Task SeeAsync(params Creature[] creatures)
    {
        for (int i = 0; i < 20 && !await OnWorldAsync(() => creatures.All(c => Player.VisibleObjects.Contains(c.Guid))); i++)
            await Host.World.AdvanceClockAsync(ThinkMs);
        if (!await OnWorldAsync(() => creatures.All(c => Player.VisibleObjects.Contains(c.Guid))))
            throw new InvalidOperationException("the bot does not see the spawned creatures");
    }

    /// <summary>One think of the brain, then <see cref="ThinkMs"/> of world time.</summary>
    public async Task ThinkAsync()
    {
        await OnWorldAsync(() =>
        {
            Session.ManagedBudget = new ManagedActionBudget(8);
            Brain.Update(ThinkMs);
            return true;
        });
        await Host.World.AdvanceClockAsync(ThinkMs);
    }

    /// <summary>Think until <paramref name="condition"/> holds (checked on the world thread after each think) or <paramref name="maxMs"/> of world time.</summary>
    public async Task<bool> ThinkUntilAsync(uint maxMs, Func<bool> condition, Action? each = null)
    {
        for (uint elapsed = 0; elapsed < maxMs; elapsed += ThinkMs)
        {
            await ThinkAsync();
            if (await OnWorldAsync(() => { each?.Invoke(); return condition(); })) return true;
        }

        return false;
    }

    public static float Distance(WorldObject a, Vector3 b) => Vector3.Distance(new Vector3(a.X, a.Y, a.Z), b);

    public async ValueTask DisposeAsync()
    {
        Brain.Stop();
        Session.Kick();
        await Session.ManagedClosed;
        await Host.DisposeAsync();
    }

    private sealed class AiStore(CreatureAiContent ai) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CreatureContent([], [], [], [], [], ai));
    }

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    private sealed class FlatGround(float z) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z2, float maxSearchDistance) => z;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z2, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }
}

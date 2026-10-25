using System.Numerics;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.OutdoorPvP;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Features;
using ArcaneCore.World.OutdoorPvP;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.GridTerrain;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.WorldPvp;

/// <summary>
/// A Silithus on open flat ground for the Silithyst tests: Kalimdor (map 1) with a Silithyst Geyser (181598) and optionally a Silithyst Mound
/// (181597) as the real data has them (type 10 goobers whose spell 29518 "Silithus Flag Click" gives the flag aura 29519), the two turn-in area
/// triggers (4162 Alliance, 4168 Horde, 4-yard spheres) and the spells the turn-in leaves behind (29534 Traces of Silithyst). Everything stands at
/// the height of the flat floor around the scenario start, 30 to 90 yards from where the bots are put. The real outdoor PvP feature, spell
/// system, game object use and area trigger handler are the ones under test; only the content is synthetic.
/// </summary>
internal sealed class SilithystWorld : IAsyncDisposable
{
    internal const uint Geyser = 181598, Mound = 181597;
    internal const uint GeyserSpawn = 1, MoundSpawn = 2;

    /// <summary>The goober's spell: 29518 "Silithus Flag Click (DND)", whose script gives the flag aura 29519.</summary>
    internal const uint ClickSpell = 29518;

    /// <summary>Where a bot is put: Silithus, 30 yards short of the geyser.</summary>
    internal static readonly Vector3 Start = new(StartX, StartY, StartZ);

    internal static readonly Vector3 GeyserAt = new(StartX + 30f, StartY, StartZ);
    internal static readonly Vector3 MoundAt = new(StartX + 30f, StartY + 12f, StartZ);
    internal static readonly Vector3 AllianceTrigger = new(StartX - 60f, StartY, StartZ);
    internal static readonly Vector3 HordeTrigger = new(StartX, StartY + 60f, StartZ);

    private SilithystWorld(ScenarioTestWorld world, PlayerbotOptions options)
    {
        World = world;
        Options = options;
    }

    internal ScenarioTestWorld World { get; }

    internal PlayerbotOptions Options { get; }

    internal OutdoorPvPFeature Pvp => World.Services.GetServices<IWorldFeature>().OfType<OutdoorPvPFeature>().Single();

    internal SilithusZone Zone => Pvp.Silithus ?? throw new InvalidOperationException("no Silithus script");

    internal static ObjectGuid GuidOf(uint entry, uint spawn) => ObjectGuid.WithEntry(HighGuid.GameObject, entry, spawn);

    internal static async Task<SilithystWorld> StartAsync(Action<PlayerbotOptions>? configure = null, bool mound = false, bool brokenClick = false)
    {
        var options = new PlayerbotOptions { Enabled = true, MaxBots = 16, AllowedMaps = [0, 1], ThinkIntervalMs = 200, MaxActionsPerTick = 16 };
        options.WorldPvp.Silithyst.ParticipationChance = 1f;
        configure?.Invoke(options);
        ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services =>
        {
            services.AddScoped<IGameObjectDataStore>(_ => new Content(mound, brokenClick));
            services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(Spells()));
            services.AddSingleton<IOptions<PlayerbotOptions>>(Microsoft.Extensions.Options.Options.Create(options));
        });
        await world.UseFlatFloorAsync();
        await world.Host.OnWorldAsync(() =>
        {
            WorldMaps maps = WorldMaps.Of(world.Host.World);
            maps.ReplaceAreaTriggerTables(maps.BuildAreaTriggerTables(InMemoryMapDataStore.Content with
            {
                AreaTriggers =
                [
                    new AreaTriggerTemplate(SilithusCatalog.AreaTriggerAlliance, 1, AllianceTrigger.X, AllianceTrigger.Y, AllianceTrigger.Z, 4f, 0, 0, 0, 0, "Silithyst Alliance"),
                    new AreaTriggerTemplate(SilithusCatalog.AreaTriggerHorde, 1, HordeTrigger.X, HordeTrigger.Y, HordeTrigger.Z, 4f, 0, 0, 0, 0, "Silithyst Horde"),
                ],
                AreaTriggerTeleports = [],
            }));
            WorldCollision.Of(world.Host.World).Install(pathfinder: new OpenPathfinder());
            return true;
        });
        await world.Host.World.AdvanceClockAsync(1_000);
        return new SilithystWorld(world, options);
    }

    /// <summary>
    /// A started bot of <paramref name="race"/> (1 human: Alliance; 2 orc: Horde) raised to <paramref name="level"/> and put in Silithus (map 1) at
    /// <paramref name="at"/> (<see cref="Start"/>): in zone 1377 and present in the outdoor PvP script when <paramref name="inZone"/>, otherwise
    /// left in zone 17 and outside it.
    /// </summary>
    internal async Task<Guid> AddBotAsync(string name, byte race, byte cls = 1, byte level = 60, bool inZone = true, Vector3? at = null)
    {
        PlayerbotOperationResult created = await World.Bots.CreateAsync(name, race, cls);
        Assert.True(created.Success, created.Code);
        PlayerbotOperationResult started = await World.Bots.StartAsync(name);
        Assert.True(started.Success, started.Code);
        Guid id = started.BotId!.Value;
        Vector3 spot = at ?? Start;
        bool teleporting = await World.Host.OnWorldAsync(() =>
        {
            Player player = PlayerOf(id);
            player.Level = level;
            player.Health = player.MaxHealth;
            return World.Services.GetRequiredService<ArcaneCore.World.Teleport.TeleportFeature>().Teleports.TeleportTo(player, 1, spot.X, spot.Y, spot.Z, 0f);
        });
        Assert.True(teleporting, "the teleport to Kalimdor was refused");
        Assert.True(await World.Host.World.AdvanceClockUntilAsync(5_000, () => PlayerOf(id).MapId == 1), "the bot never reached Kalimdor");
        await World.Host.OnWorldAsync(() =>
        {
            Player player = PlayerOf(id);
            player.ZoneId = inZone ? SilithusCatalog.SilithusZone : 17;
            if (inZone) Pvp.OnZoneChanged(player, 0, SilithusCatalog.SilithusZone, 0, null);
            return true;
        });
        return id;
    }

    internal Player PlayerOf(Guid id) => World.Bots.FindSession(id)?.Player ?? throw new InvalidOperationException("bot not running");

    internal PlayerbotBrain BrainOf(Guid id) => World.Bots.FindBrain(id) ?? throw new InvalidOperationException("bot not running");

    internal Task<T> OnWorldAsync<T>(Func<T> read) => World.Host.OnWorldAsync(read);

    internal Task<bool> RunUntilAsync(uint maxMs, Func<bool> condition) => World.Host.World.AdvanceClockUntilAsync(maxMs, condition);

    internal void RemoveAura(Player player, uint spell) => World.Services.GetRequiredService<SpellFeature>().System.RemoveAuras(player, spell);

    internal bool HasAura(Player player, uint spell) => World.Services.GetRequiredService<SpellFeature>().System.HasAura(player, spell);

    internal Task<bool> CastOnSelfAsync(Guid id, uint spell) => OnWorldAsync(() =>
        World.Services.GetRequiredService<SpellFeature>().System.CastSpell(PlayerOf(id), spell, SpellCastTargets.ForSelf(), triggered: true) == SpellCastResult.CastOk);

    public ValueTask DisposeAsync() => World.DisposeAsync();

    // --- content ----------------------------------------------------------------------------------------------------

    private static SpellContent Spells()
    {
        SpellContent baseContent = ScenarioTestContent.Spells();
        return baseContent with
        {
            Spells =
            [
                .. baseContent.Spells,
                // "Silithus Flag Click (DND)": a dummy effect at the user (the script gives the flag), in the shape of the banner trigger spells.
                new SpellTemplateRow
                {
                    Id = ClickSpell, SpellName = "Silithus Flag Click (DND)", Attributes = 0x100, AttributesEx2 = 4, CastingTimeIndex = 1,
                    RangeIndex = 6, Effect1 = 3, EffectImplicitTargetA1 = 25,
                },
                Aura(SilithusCatalog.SpellSilithystFlag, "Silithyst", durationIndex: 21),
                Aura(SilithusCatalog.SpellTracesOfSilithyst, "Traces of Silithyst", durationIndex: 4),
                Aura(SilithusCatalog.SpellFlagDrop, "Silithyst drop", durationIndex: 4),
            ],
            CastTimes = Missing(baseContent.CastTimes, c => c.Id, new SpellCastTimeRow { Id = 1 }),
            Durations = Missing(baseContent.Durations, d => d.Id, new SpellDurationRow { Id = 21, Duration = -1, MaxDuration = -1 },
                new SpellDurationRow { Id = 4, Duration = 900_000, MaxDuration = 900_000 }),
            Ranges = Missing(baseContent.Ranges, r => r.Id, new SpellRangeRow { Id = 1 }, new SpellRangeRow { Id = 6, MaxRange = 100 }),
        };
    }

    private static T[] Missing<T>(IReadOnlyList<T> existing, Func<T, uint> id, params T[] wanted)
        => [.. existing, .. wanted.Where(w => existing.All(e => id(e) != id(w)))];

    private static SpellTemplateRow Aura(uint id, string name, uint durationIndex) => new()
    {
        Id = id, SpellName = name, RangeIndex = 1, DurationIndex = durationIndex, Effect1 = 6, EffectImplicitTargetA1 = 1, EffectApplyAuraName1 = 4,
    };

    /// <summary>The geyser (and the mound) as type 10 goobers casting 29518 (data10), unlocked, with the spawns of the test.</summary>
    private sealed class Content(bool mound, bool brokenClick) : IGameObjectDataStore
    {
        public Task<GameObjectContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            GameObjectTemplate Goober(uint entry, string name)
            {
                uint[] data = new uint[GameObjectTemplate.DataCount];
                data[10] = brokenClick ? 0u : ClickSpell;
                data[11] = 1;
                return new GameObjectTemplate { Entry = entry, Type = (uint)GameObjectType.Goober, DisplayId = 6271, Name = name, Data = data };
            }

            static GameObjectSpawn Spawn(uint spawn, uint entry, Vector3 at)
                => new() { Guid = spawn, Entry = entry, MapId = 1, X = at.X, Y = at.Y, Z = at.Z, SpawnTimeSeconds = 300 };

            GameObjectSpawn[] spawns = mound ? [Spawn(GeyserSpawn, Geyser, GeyserAt), Spawn(MoundSpawn, Mound, MoundAt)] : [Spawn(GeyserSpawn, Geyser, GeyserAt)];
            return Task.FromResult(new GameObjectContent([Goober(Geyser, "Silithyst Geyser"), Goober(Mound, "Silithyst Mound")], spawns, [], [], []));
        }
    }

    private sealed class OpenPathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => PathResult.StraightLine(start, end, PathType.Normal);
    }
}

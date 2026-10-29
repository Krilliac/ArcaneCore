using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Teleport;

/// <summary>
/// The mount allow check on far arrival (vmangos HandleMoveWorldportAckOpcode, MovementHandler.cpp:189-191) through the real
/// <see cref="TeleportFeature"/> wiring: the feature's own <c>SendAfterAddToMap</c> callback must resolve the <see cref="SpellFeature"/> from the
/// service provider and run <see cref="MapEntryMountRule.Enforce"/>. The Game-level tests build a <see cref="TeleportService"/> with a hand-written
/// callback and cannot see that wiring go missing.
/// </summary>
public sealed class TeleportFeatureMapEntryMountTests
{
    private const uint BrownHorse = 960101;
    private const uint HorseCreature = 2402;
    private const uint HorseDisplay = 14337;
    private const uint Deadmines = 36;

    private sealed class Displays : IMountDisplaySource
    {
        public uint? FindMountDisplay(uint creatureEntry) => creatureEntry == HorseCreature ? HorseDisplay : null;
    }

    private sealed class Maps : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [
                new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
                new MapTemplate(Deadmines, 0, MapType.Instance, 0, 5, 0, 0, -11208f, 1672f, "Deadmines", ""),
            ],
            [], [], [], []));
    }

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _services;
        private int _next = 1;

        public Rig()
        {
            ServiceCollection collection = new();
            collection.AddLogging();
            collection.AddSingleton<ILogger<TeleportFeature>>(NullLogger<TeleportFeature>.Instance);
            collection.AddSingleton<IMapDataStore, Maps>();
            collection.AddSingleton<TeleportFeature>();
            collection.AddSingleton(sp => new SpellFeature(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellFeature>.Instance));
            _services = collection.BuildServiceProvider();
            World = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
                new CharacterSaveQueue(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
                NullLogger<WorldRuntime>.Instance);
            Teleports = _services.GetRequiredService<TeleportFeature>();
            Teleports.Attach(World);
            Spells = _services.GetRequiredService<SpellFeature>();
            Spells.Attach(World);
            Spells.System.Store = new SpellStore([new SpellInfo
            {
                Id = BrownHorse,
                SpellVisual = 1,
                Duration = new SpellDuration(-1, 0, -1),
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Mounted, MiscValue = (int)HorseCreature }, new(), new()],
            }], [], []);
            LocomotionEnvironment.RegisterMountDisplays(World, new Displays());
        }

        public WorldRuntime World { get; }

        public TeleportFeature Teleports { get; }

        public SpellFeature Spells { get; }

        public Player AddMountedPlayer()
        {
            int id = _next++;
            var character = new CharacterRecord
            {
                Id = id, AccountId = 1, Name = "Tfmount" + id, Race = 1, Class = 1, Gender = 0, Level = 60, MapId = 0,
            };
            var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new NullPlayerSession());
            player.Relocate(1f, 1f, 1f, 0f, 0);
            World.AddPlayer(player);
            Spells.System.CastSpell(player, BrownHorse, SpellCastTargets.ForSelf(), triggered: true);
            Assert.True(Spells.System.HasAura(player, BrownHorse));
            Assert.Equal(HorseDisplay, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
            return player;
        }

        public void FarTeleport(Player player, uint mapId)
        {
            TeleportService teleports = Teleports.Teleports;
            Assert.True(teleports.TeleportTo(player, mapId, 10f, 10f, 10f, 0));
            World.RunTick(50);
            Assert.Equal(TeleportStage.Far, teleports.StageOf(player));
            Assert.True(teleports.HandleWorldportAck(player));
            World.RunTick(50);
            Assert.Equal(mapId, player.Map!.MapId);
        }

        public void Dispose()
        {
            World.Dispose();
            _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void AFarTeleportIntoADungeon_ThroughTheFeature_RemovesTheMountedAuraAndDisplay()
    {
        using var rig = new Rig();
        Player player = rig.AddMountedPlayer();

        rig.FarTeleport(player, Deadmines);

        Assert.False(rig.Spells.System.HasAura(player, BrownHorse));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
    }

    [Fact]
    public void AFarTeleportToAnotherContinent_ThroughTheFeature_KeepsTheMount()
    {
        using var rig = new Rig();
        Player player = rig.AddMountedPlayer();

        rig.FarTeleport(player, 1);

        Assert.True(rig.Spells.System.HasAura(player, BrownHorse));
        Assert.Equal(HorseDisplay, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
    }
}

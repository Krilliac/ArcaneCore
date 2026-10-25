using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.OutdoorPvP;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.TestSupport;
using ArcaneCore.World.OutdoorPvP;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.OutdoorPvP;

/// <summary>
/// The Silithyst flag (29519) leaving a carrier in Silithus (vmangos SilithystAuraScript, spell_special.cpp:371-380): a cancel, death or dispel
/// drops the Silithyst Mound (29533 → gameobject 181597); a turn-in, an expiry or the mount/stealth strip does not add a second one.
/// </summary>
public sealed class SilithystFlagDropTests
{
    private const uint MountSpell = 991201;
    private const uint StealthSpell = 991202;
    private const uint MoundEntry = 181597;

    [Theory]
    [InlineData(AuraRemoveMode.Cancel, 1)]
    [InlineData(AuraRemoveMode.Dispel, 1)]
    [InlineData(AuraRemoveMode.Death, 1)]
    [InlineData(AuraRemoveMode.Expire, 0)]
    [InlineData(AuraRemoveMode.Default, 0)]
    public void Losing_the_flag_drops_a_mound_only_for_cancel_dispel_or_death(AuraRemoveMode mode, int mounds)
    {
        using Rig rig = new();
        Player carrier = rig.AddCarrier();
        Assert.True(rig.Spells.System.HasAura(carrier, SilithusCatalog.SpellSilithystFlag));

        if (mode == AuraRemoveMode.Death)
        {
            carrier.Health = 0;
            rig.Spells.System.OnUnitDied(carrier);
        }
        else
        {
            rig.Spells.System.RemoveAuras(carrier, SilithusCatalog.SpellSilithystFlag, mode);
        }

        Assert.False(rig.Spells.System.HasAura(carrier, SilithusCatalog.SpellSilithystFlag));
        Assert.Equal(mounds, rig.Mounds().Count);
        foreach (GameObject mound in rig.Mounds())
        {
            Assert.Equal(carrier.X, mound.X);
            Assert.Equal(carrier.Y, mound.Y);
        }
    }

    [Fact]
    public void A_turn_in_removes_the_flag_without_a_mound()
    {
        using Rig rig = new();
        Player carrier = rig.AddCarrier();

        rig.Feature.OnAreaTrigger(carrier, SilithusCatalog.AreaTriggerAlliance);

        Assert.False(rig.Spells.System.HasAura(carrier, SilithusCatalog.SpellSilithystFlag));
        Assert.Equal(1u, rig.Feature.Silithus!.GatheredAlliance);
        Assert.Empty(rig.Mounds());
    }

    [Fact]
    public void Mounting_with_the_flag_drops_exactly_one_mound()
    {
        using Rig rig = new();
        Player carrier = rig.AddCarrier();

        Assert.Equal(SpellCastResult.CastOk, rig.Spells.System.CastSpell(carrier, MountSpell, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Single(rig.Mounds());
    }

    [Theory]
    [InlineData(AuraRemoveMode.Cancel)]
    [InlineData(AuraRemoveMode.Dispel)]
    public void A_cancelled_or_dispelled_flag_followed_by_stealth_or_mount_in_the_same_tick_drops_exactly_one_mound(AuraRemoveMode mode)
    {
        foreach (uint follow in new[] { StealthSpell, MountSpell })
        {
            using Rig rig = new();
            Player carrier = rig.AddCarrier();

            rig.Spells.System.RemoveAuras(carrier, SilithusCatalog.SpellSilithystFlag, mode);
            Assert.Equal(SpellCastResult.CastOk, rig.Spells.System.CastSpell(carrier, follow, SpellCastTargets.ForSelf(), triggered: true));

            Assert.Single(rig.Mounds());
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly GameObjectMapSystem _objects;
        private int _nextId = 1;

        public Rig()
        {
            ServiceCollection collection = new();
            collection.AddLogging();
            collection.AddSingleton<ILogger<TeleportFeature>>(NullLogger<TeleportFeature>.Instance);
            collection.AddSingleton<TeleportFeature>();
            collection.AddSingleton(sp => new SpellFeature(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellFeature>.Instance));
            _services = collection.BuildServiceProvider();
            World = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
                new CharacterSaveQueue(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
                NullLogger<WorldRuntime>.Instance);
            _services.GetRequiredService<TeleportFeature>().Attach(World);
            Spells = _services.GetRequiredService<SpellFeature>();
            Spells.Attach(World);
            Spells.System.Store = new SpellStore([
                Spell(SilithusCatalog.SpellSilithystFlag, SpellEffectName.ApplyAura, AuraType.Dummy, 0),
                Spell(SilithusCatalog.SpellFlagDrop, SpellEffectName.SummonObjectWild, AuraType.None, (int)MoundEntry),
                Spell(MountSpell, SpellEffectName.ApplyAura, AuraType.Mounted, 0),
                Spell(StealthSpell, SpellEffectName.ApplyAura, AuraType.ModStealth, 0),
            ], [], []);
            Feature = new OutdoorPvPFeature(_services, NullLogger<OutdoorPvPFeature>.Instance);
            Feature.Attach(World);
            World.RunTick(1);
            Map kalimdor = World.GetMap(SilithusCatalog.MapId);
            var template = new GameObjectTemplate
            {
                Entry = MoundEntry, Type = (uint)GameObjectType.Generic, DisplayId = 6500, Name = "Silithyst Mound", Size = 1.0f,
                Data = new uint[GameObjectTemplate.DataCount],
            };
            _objects = new GameObjectMapSystem(kalimdor, new GameObjectContent([template], [], [], [], []));
            kalimdor.AddUpdater(_objects);
        }

        public WorldRuntime World { get; }

        public SpellFeature Spells { get; }

        public OutdoorPvPFeature Feature { get; }

        public Player AddCarrier()
        {
            int id = _nextId++;
            var character = new CharacterRecord
            {
                Id = id, AccountId = 1, Name = "Silcarrier" + id, Race = 1, Class = 1, Gender = 0, Level = 60,
                MapId = SilithusCatalog.MapId, ZoneId = SilithusCatalog.SilithusZone,
            };
            var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new NullPlayerSession());
            player.Relocate(-6800f, 1200f, 10f, 1f, 0);
            World.AddPlayer(player);
            World.NotifyLoggedIn(player);
            Feature.OnZoneChanged(player, 0, SilithusCatalog.SilithusZone, 0, null);
            Assert.Equal(SpellCastResult.CastOk, Spells.System.CastSpell(player, SilithusCatalog.SpellSilithystFlag, SpellCastTargets.ForSelf(), triggered: true));
            return player;
        }

        public List<GameObject> Mounds() => [.. _objects.GameObjects.Where(go => go.Template.Entry == MoundEntry)];

        // Real data (Spell.dbc 5875): 29533 carries Attributes 0x09800000 (AllowCastWhileDead 0x00800000, so a dead carrier still drops) and
        // TargetA 47 (a location in front of the caster); 29519 has AuraInterruptFlags 0x003A0000, so mount and stealth strip it (Default).
        private static SpellInfo Spell(uint id, SpellEffectName effect, AuraType aura, int miscValue) => new()
        {
            Attributes = id == SilithusCatalog.SpellFlagDrop ? (SpellAttributes)0x09800000 : default,
            AuraInterruptFlags = id == SilithusCatalog.SpellSilithystFlag ? (SpellAuraInterruptFlags)0x003A0000 : default,
            Id = id, Name = $"Silithyst test {id}", RangeIndex = 4, Range = new SpellRange(0, 30),
            Duration = aura == AuraType.None ? default : new SpellDuration(200000, 0, 200000),
            Effects = [new SpellEffectInfo
            {
                Effect = effect, BasePoints = 0, BaseDice = 1, DieSides = 1, TargetA = id == SilithusCatalog.SpellFlagDrop ? (SpellImplicitTarget)47 : SpellImplicitTarget.UnitCaster, AuraType = aura, MiscValue = miscValue,
            }, new(), new()],
        };

        public void Dispose()
        {
            World.Dispose();
            _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

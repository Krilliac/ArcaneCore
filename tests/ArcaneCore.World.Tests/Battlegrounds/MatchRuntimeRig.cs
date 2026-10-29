using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Battlegrounds;

/// <summary>
/// A Warsong Gulch match in a bare world: the battleground feature (manager, match runtime, match map instance) next to the spell feature, with
/// the creature and game object systems of the match map added by hand (the world is never ticked, so the tests drive
/// <see cref="MatchRuntime.Update"/> themselves and the match's clock is the sum of their diffs).
/// </summary>
internal sealed class MatchRuntimeRig : IDisposable
{
    public const uint PlainCreature = 990301;
    public const uint SpiritGuideCreature = 13117;      // Warsong Spirit Guide
    public const uint PlainObject = 990311;
    public const uint BuffTrap = 990312;
    public const uint BuffObject = 990313;
    public const uint BuffSpell = 990321;
    public const uint SpiritHealChannel = BattlegroundConstants.SpellSpiritHealChannel;

    private readonly ServiceProvider _services;
    private int _nextPlayer = 1;

    public MatchRuntimeRig()
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
        Spells.System.Store = new SpellStore([SpiritHealChannelSpell(), BuffAuraSpell()], [], []);

        Feature = new BattlegroundFeature(_services, _services.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
        Feature.Attach(World);
        Feature.Manager.RegisterTemplate(new BattlegroundTemplate
        {
            Type = BattlegroundType.WarsongGulch,
            MapId = BattlegroundManager.MapOfType(BattlegroundType.WarsongGulch),
            Name = "Warsong Gulch",
            MinPlayersPerTeam = 5,
            MaxPlayersPerTeam = 10,
            MinLevel = 10,
            MaxLevel = 60,
            AllianceStart = new BattlegroundStartLocation(1f, 2f, 3f, 0f),
            HordeStart = new BattlegroundStartLocation(4f, 5f, 6f, 0f),
        });
        Battleground = Feature.Manager.CreateNewBattleground(BattlegroundType.WarsongGulch, 0) ?? throw new InvalidOperationException("no match");
        Match = Feature.FindMatch(Battleground.InstanceId) ?? throw new InvalidOperationException("no match runtime");
        Map = Match.Map ?? throw new InvalidOperationException("the match has no map");

        Creatures = Map.FindUpdater<CreatureMapSystem>() ?? Add(new CreatureMapSystem(Map, new CreatureContent([PlainTemplate(), GuideTemplate()], [], [], [], []), random: new Random(1)));
        Objects = Map.FindUpdater<GameObjectMapSystem>() ?? Add(new GameObjectMapSystem(Map, new GameObjectContent([ObjectTemplate(PlainObject, GameObjectType.Generic, 0, 0), TrapTemplate(), ObjectTemplate(BuffObject, GameObjectType.Generic, 0, 0)], [], [], [], [])));
        Match.Update(0);   // hooks the two systems (AttachMap)
    }

    public WorldRuntime World { get; }

    public SpellFeature Spells { get; }

    public BattlegroundFeature Feature { get; }

    public Battleground Battleground { get; }

    public MatchRuntime Match { get; }

    public Map Map { get; }

    public CreatureMapSystem Creatures { get; }

    public GameObjectMapSystem Objects { get; }

    private T Add<T>(T updater) where T : class, IMapUpdater
    {
        Map.AddUpdater(updater);
        return updater;
    }

    public Creature AddCreature(uint entry, float x, float y) => Creatures.SpawnTemporary(Creatures.Content.FindTemplate(entry)!, x, y, 10f, 0f);

    public GameObject AddObject(uint entry, float x, float y)
        => Objects.Summon(entry, x, y, 10f, 0f) ?? throw new InvalidOperationException($"object {entry} did not spawn");

    /// <summary>A player on the match map who belongs to no team of the match (a game master, or anyone else who walked in).</summary>
    public Player AddPlayer(float x, float y)
    {
        int id = _nextPlayer++;
        var character = new CharacterRecord
        {
            Id = id, AccountId = 1, Name = "Mrtplayer" + id, Race = 1, Class = 1, Gender = 0, Level = 60, MapId = Map.MapId,
        };
        var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new NullPlayerSession());
        player.Relocate(x, y, 10f, 0f, 0);
        World.AddPlayer(player, Map);
        return player;
    }

    public void Dispose()
    {
        Feature.StopAsync().GetAwaiter().GetResult();
        World.Dispose();
        _services.DisposeAsync().AsTask().GetAwaiter().GetResult();   // disposes the spell feature (async-only)
    }

    private static CreatureTemplate PlainTemplate() => new()
    {
        Entry = PlainCreature, Name = "Match Test Creature", MinLevel = 60, MaxLevel = 60, MinLevelHealth = 10000, MaxLevelHealth = 10000,
        Faction = 67, NpcFlags = 0, Civilian = false,
    };

    private static CreatureTemplate GuideTemplate() => new()
    {
        Entry = SpiritGuideCreature, Name = "Warsong Spirit Guide", MinLevel = 60, MaxLevel = 60, MinLevelHealth = 10000, MaxLevelHealth = 10000,
        Faction = 67, NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.SpiritGuide), Civilian = false,
    };

    private static GameObjectTemplate ObjectTemplate(uint entry, GameObjectType type, uint spell, uint cooldown)
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[3] = spell;
        data[5] = cooldown;
        return new GameObjectTemplate { Entry = entry, Type = (uint)type, DisplayId = 6500, Name = "Match test object " + entry, Size = 1.0f, Data = data };
    }

    /// <summary>A battleground buff trap: data2 (charges) 0 and data5 (cooldown) 3 s are what MatchRuntime.UpdateBuffTraps looks for, data3 is its spell.</summary>
    private static GameObjectTemplate TrapTemplate() => ObjectTemplate(BuffTrap, GameObjectType.Trap, BuffSpell, BattlegroundConstants.BattlegroundTrapCooldownSeconds);

    /// <summary>Spirit Heal Channel as the 1.12.1 client data has it (client-dbc-5875 Spell.dbc, see SpiritGuideWaveTests): a 30 s self channel.</summary>
    private static SpellInfo SpiritHealChannelSpell() => new()
    {
        Id = SpiritHealChannel, Name = "Spirit Heal Channel", RangeIndex = SpellConstants.RangeIndexSelfOnly, Range = new SpellRange(0, 0),
        AttributesEx = SpellAttributesEx.IsSelfChanneled, Duration = new SpellDuration(30_000, 0, 30_000), SpellVisual = 1,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster, AuraType = AuraType.PeriodicTriggerSpell, Amplitude = 30_000, TriggerSpell = 22012 }, new(), new()],
    };

    /// <summary>A synthetic buff: a Dummy aura on the caster (the trap's spell is cast on the player itself).</summary>
    private static SpellInfo BuffAuraSpell() => new()
    {
        Id = BuffSpell, Name = "Match test buff", RangeIndex = SpellConstants.RangeIndexSelfOnly, Range = new SpellRange(0, 0),
        Duration = new SpellDuration(60_000, 0, 60_000), SpellVisual = 1,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster, AuraType = AuraType.Dummy }, new(), new()],
    };

    /// <summary>Whether the guide is channelling Spirit Heal Channel.</summary>
    public bool IsChanneling(Creature guide) => Spells.System.IsChanneling(guide);

    /// <summary>
    /// The population the allocation scenarios share: <paramref name="creatures"/> plain creatures and a spirit guide that is already
    /// channelling, <paramref name="objects"/> plain objects and one buff trap with no player within range (one far away on the map),
    /// plus pending match objects and events that are not due for a long while.
    /// </summary>
    public void BuildSteadyState(int creatures, int objects)
    {
        for (int i = 0; i < creatures; i++)
        {
            AddCreature(PlainCreature, 5f + (i % 25), 5f + (i / 25 % 25));
        }

        Creature guide = AddCreature(SpiritGuideCreature, 3f, 3f);
        for (int i = 0; i < objects; i++)
        {
            AddObject(PlainObject, 5f + (i % 10), 5f + (i / 10));
        }

        AddObject(BuffTrap, 40f, 40f);
        AddPlayer(400f, 400f);
        for (int i = 0; i < 4; i++)
        {
            Match.AddBattlegroundObject(i, BuffObject, 20f + i, 20f, 10f, 0f);
            Match.SpawnBattlegroundObject(i, 100_000);     // due at 100 000 s of match clock
        }

        Match.EventStateChanged(1, 1, spawn: true, forcedDespawn: false, respawnDelaySeconds: 100_000);
        Match.EventStateChanged(1, 2, spawn: true, forcedDespawn: false, respawnDelaySeconds: 100_000);

        Match.Update(50);                                   // the guide starts channelling
        Assert.True(IsChanneling(guide), "the spirit guide should be channelling after the first update");
        Assert.Equal(creatures + 1, Creatures.Creatures.Count);
        Assert.Equal(objects + 1, Objects.GameObjects.Count);
    }
}

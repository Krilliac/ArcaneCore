using System.Globalization;
using System.Text.RegularExpressions;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Naxxramas;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The Naxxramas boss and event spells that had no handler or consumer: the spell scripts of Sapphiron's Summon Blizzard, Thaddius's Magnetic Pull,
/// Gluth's zombie pulses and Gothik's side checks, and the list that proves every spell of the lane is implemented, consumed or inert.
/// </summary>
public sealed class NaxxramasSpellTests
{
    private const uint SummonBlizzard = 28560, MagneticPullA = 28338, MagneticPullB = 28339, ZombieChowSearch = 28236, CallAllZombieChow = 29682;
    private const uint CheckSpectralSide = 28749, CheckUnrelentingSide = 29875, SpectralSideAssault = 28781, UnrelentingSideAssault = 29874;

    /// <summary>Every spell of the lane (spell-lane-naxxramas-casts).</summary>
    private static readonly uint[] s_laneSpells =
    [
        28236, 29682, 28560, 29357, 28471, 28617, 28033, 28526, 28338, 28307, 28376, 28749, 28781, 28797, 29874, 29875, 29422,
    ];

    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo ScriptEffect(uint id, SpellImplicitTarget target = SpellImplicitTarget.UnitCaster)
        => Instant(SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0, target)));

    private static SpellInfo DummyEffect(uint id, SpellImplicitTarget target = SpellImplicitTarget.UnitCaster)
        => Instant(SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.Dummy, 0, target)));

    private static SpellInfo Marker(uint id) =>
        Instant(SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(30_000, 0, 30_000),
        });

    /// <summary>With spell batching on, a script's cast is held until the next window boundary: advance to it. A no-op with the window at 0.</summary>
    private static void FlushSpellBatch(SpellTestKit kit)
    {
        uint window = kit.System.SpellBatchWindowMs;
        if (window != 0)
        {
            kit.Advance(window - (kit.Now % window));
        }
    }

    private static SpellTestKit Kit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        kit.System.Units = new MapObjectResolver();
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        kit.World.GetMap(0).Combat.SpellMitigation = kit.System;
        return kit;
    }

    /// <summary>Temporary creatures of the given entries on the kit's map, in order, each 10 000 health, on the player's row at the given X.</summary>
    private static Creature[] SpawnCreatures(SpellTestKit kit, params (uint Entry, float X)[] units)
    {
        CreatureTemplate[] templates = [.. units.Select(u => u.Entry).Distinct().Select(e => CreatureTestKit.Template(e, t =>
        {
            t.MinLevelHealth = 10_000;
            t.MaxLevelHealth = 10_000;
        }))];
        var content = new CreatureContent(templates, [], [], [], []);
        var system = new CreatureMapSystem(kit.World.GetMap(0), content, random: new Random(1));
        kit.World.GetMap(0).AddUpdater(system);
        kit.World.RunTick(50);
        return [.. units.Select(u => system.SpawnTemporary(content.FindTemplate(u.Entry)!, u.X, 0, 83.5f, 0))];
    }

    [Fact]
    public void SummonBlizzard_EachUnitTheSpellReachesCastsTheBlizzardSummonOnItself()
    {
        using SpellTestKit kit = Kit(ScriptEffect(SummonBlizzard), Marker(SapphironSummonBlizzardScript.SummonBlizzard));
        (Player player, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, SummonBlizzard, SpellCastTargets.ForSelf(), triggered: true));
        FlushSpellBatch(kit);

        Assert.True(kit.System.HasAura(player, SapphironSummonBlizzardScript.SummonBlizzard));
    }

    [Fact]
    public void MagneticPullA_TheHitUnitCastsTheJumpAtTheCastersVictim()
    {
        using SpellTestKit kit = Kit(ScriptEffect(MagneticPullA, SpellImplicitTarget.Unit), Marker(ThaddiusMagneticPullScript.MagneticPullJump));
        (Player victim, _) = kit.AddPlayer(1);
        (Player pulled, _) = kit.AddPlayer(2, 2, 0);
        Creature add = SpawnCreatures(kit, (CreatureTestKit.WolfEntry, 4))[0];
        Assert.True(kit.World.GetMap(0).Combat.Attack(add, victim));
        Assert.Same(victim, add.Combat.Victim);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(add, MagneticPullA, SpellCastTargets.ForUnit(pulled.Guid), triggered: true));
        FlushSpellBatch(kit);

        Assert.True(kit.System.HasAura(pulled, ThaddiusMagneticPullScript.MagneticPullJump));
        Assert.False(kit.System.HasAura(victim, ThaddiusMagneticPullScript.MagneticPullJump));
    }

    [Fact]
    public void MagneticPullB_TheHitUnitCastsTheJumpAtTheCastersVictim()
    {
        using SpellTestKit kit = Kit(ScriptEffect(MagneticPullB, SpellImplicitTarget.Unit), Marker(ThaddiusMagneticPullScript.MagneticPullJump));
        (Player victim, _) = kit.AddPlayer(1);
        (Player pulled, _) = kit.AddPlayer(2, 2, 0);
        Creature add = SpawnCreatures(kit, (CreatureTestKit.WolfEntry, 4))[0];
        Assert.True(kit.World.GetMap(0).Combat.Attack(add, victim));
        Assert.Same(victim, add.Combat.Victim);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(add, MagneticPullB, SpellCastTargets.ForUnit(pulled.Guid), triggered: true));
        FlushSpellBatch(kit);

        Assert.True(kit.System.HasAura(pulled, ThaddiusMagneticPullScript.MagneticPullJump));
        Assert.False(kit.System.HasAura(victim, ThaddiusMagneticPullScript.MagneticPullJump));
    }

    [Fact]
    public void MagneticPullB_WithoutAVictim_CastsNoJump()
    {
        using SpellTestKit kit = Kit(ScriptEffect(MagneticPullB, SpellImplicitTarget.Unit), Marker(ThaddiusMagneticPullScript.MagneticPullJump));
        (Player pulled, _) = kit.AddPlayer(2, 2, 0);
        Creature add = SpawnCreatures(kit, (CreatureTestKit.WolfEntry, 4))[0];
        Assert.Null(add.Combat.Victim);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(add, MagneticPullB, SpellCastTargets.ForUnit(pulled.Guid), triggered: true));
        FlushSpellBatch(kit);

        Assert.False(kit.System.HasAura(pulled, ThaddiusMagneticPullScript.MagneticPullJump));
    }

    [Fact]
    public void ZombieChowSearch_GluthKillsAZombieInRangeAndGainsFivePercentOfHealth()
    {
        using SpellTestKit kit = Kit(ScriptEffect(ZombieChowSearch, SpellImplicitTarget.Unit));
        kit.AddPlayer(1);
        Creature[] units = SpawnCreatures(kit, (15932, 1), (GluthZombieChowSearchScript.ZombieChow, 6));
        (Creature gluth, Creature zombie) = (units[0], units[1]);
        gluth.Health = gluth.MaxHealth / 2;
        uint before = gluth.Health;

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(gluth, ZombieChowSearch, SpellCastTargets.ForUnit(zombie.Guid), triggered: true));
        FlushSpellBatch(kit);

        Assert.False(zombie.IsAlive);
        Assert.Equal(before + gluth.MaxHealth / 20, gluth.Health);
    }

    [Fact]
    public void ZombieChowSearch_SparesAZombieOutOfRangeAndOtherCreatures()
    {
        using SpellTestKit kit = Kit(ScriptEffect(ZombieChowSearch, SpellImplicitTarget.Unit));
        kit.AddPlayer(1);
        Creature[] units = SpawnCreatures(kit, (15932, 0), (GluthZombieChowSearchScript.ZombieChow, 16), (CreatureTestKit.WolfEntry, 3));
        Creature gluth = units[0];
        gluth.Health = gluth.MaxHealth / 2;
        uint before = gluth.Health;

        foreach (Creature target in new[] { units[1], units[2] })
        {
            Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(gluth, ZombieChowSearch, SpellCastTargets.ForUnit(target.Guid), triggered: true));
            FlushSpellBatch(kit);
            Assert.True(target.IsAlive);
        }

        Assert.Equal(before, gluth.Health);
    }

    [Fact]
    public void CallAllZombieChow_TheReachedZombieFollowsGluth()
    {
        using SpellTestKit kit = Kit(DummyEffect(CallAllZombieChow, SpellImplicitTarget.Unit));
        kit.AddPlayer(1);
        Creature[] units = SpawnCreatures(kit, (15932, 1), (GluthZombieChowSearchScript.ZombieChow, 30));
        (Creature gluth, Creature zombie) = (units[0], units[1]);
        Assert.False(zombie.Motion.HasActive(MovementGeneratorType.Follow));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(gluth, CallAllZombieChow, SpellCastTargets.ForUnit(zombie.Guid), triggered: true));
        FlushSpellBatch(kit);

        Assert.True(zombie.Motion.HasActive(MovementGeneratorType.Follow));
    }

    /// <summary>Map 533 with the Naxxramas instance script, the spells given and one player (the tank) on the living side of Gothik's gate.</summary>
    private sealed class Raid : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public Map Map { get; }
        public NaxxramasInstance Instance { get; }
        public CreatureMapSystem Creatures { get; }
        public SpellSystem Spells { get; }
        public Player Tank { get; }

        public Raid(SpellInfo[] spells)
        {
            WorldMaps.Of(World).Load(new MapContent(
                [new MapTemplate(533, 0, MapType.Raid, 3456, 40, 7, 0, 3005.68f, -3447.77f, "Naxxramas", "instance_naxxramas")], [], [], [], []));
            Map = World.GetMap(533);
            Instance = Assert.IsType<NaxxramasInstance>(InstanceScriptRegistry.Default.Create(Map));
            Instance.Initialize();
            Map.AddUpdater(Instance);
            Spells = new SpellSystem(new SpellStore(spells, [], [], []), () => 10000) { Units = new MapObjectResolver() };
            SpellScriptDispatcher.Install(Spells, new SpellScriptRegistry([new GothikCheckSideScript(), new GothikSideAssaultScript()]));
            Creatures = new CreatureMapSystem(Map, CreatureTestKit.Content([CreatureTestKit.Template(16124), CreatureTestKit.Template(16127)], []),
                new CreatureOptions { AggroRate = 0, RespawnPacifyMs = 0 }, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = new SpellSystemCreatureCaster(Spells), Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            var objects = new GameObjectMapSystem(Map, new GameObjectContent([GameObjectTestKit.GoTemplate(181170, GameObjectType.Door)], [], [], [], []));
            Map.AddUpdater(objects);
            objects.Summon(181170, 0, 0, 270, 0); // Gothik's combat gate: Y <= 0 is the living side
            Tank = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), 533);
            Tank.Relocate(0, 0, 270, 0, 0);
            World.AddPlayer(Tank);
            World.RunTick(0);
        }

        public Creature Spawn(uint entry) => Creatures.SpawnTemporary(Creatures.Content.FindTemplate(entry)!, 0, -10, 270, 0);

        public void Cast(Unit caster, uint spell, Unit target)
        {
            Assert.Equal(SpellCastResult.CastOk, Spells.CastSpell(caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered: true));
            for (int i = 0; i < 5; i++)
            {
                World.RunTick(100);
            }
        }

        public void Dispose() => World.Dispose();
    }

    [Theory]
    [InlineData(CheckSpectralSide)]
    [InlineData(CheckUnrelentingSide)]
    public void CheckSide_WithTheGateOpen_TheAddFightsTheWholeZone(uint spell)
    {
        using var raid = new Raid([DummyEffect(spell, SpellImplicitTarget.Unit)]);
        Creature add = raid.Spawn(16124);
        Assert.NotEqual(EncounterState.InProgress, raid.Instance.GetData(NaxxramasInstance.Gothik));

        raid.Cast(add, spell, raid.Tank);

        Assert.True(add.Combat.IsInCombat);
    }

    [Fact]
    public void CheckUnrelentingSide_WithTheGateClosed_TheLivingSideAddAttacksAPlayerOnItsSide()
    {
        using var raid = new Raid([DummyEffect(CheckUnrelentingSide, SpellImplicitTarget.Unit)]);
        raid.Instance.SetData(NaxxramasInstance.Gothik, EncounterState.InProgress);
        Assert.Equal(EncounterState.InProgress, raid.Instance.GetData(NaxxramasInstance.Gothik));
        Assert.True(raid.Instance.IsOnGothikLivingSide(raid.Tank));
        Creature add = raid.Spawn(16124);

        raid.Cast(add, CheckUnrelentingSide, raid.Tank);

        Assert.Same(raid.Tank, add.Combat.Victim);
    }

    [Fact]
    public void CheckSpectralSide_WithTheGateClosed_TheAddIgnoresAPlayerOnTheLivingSide()
    {
        using var raid = new Raid([DummyEffect(CheckSpectralSide, SpellImplicitTarget.Unit)]);
        raid.Instance.SetData(NaxxramasInstance.Gothik, EncounterState.InProgress);
        Creature add = raid.Spawn(16127);

        raid.Cast(add, CheckSpectralSide, raid.Tank);

        Assert.Null(add.Combat.Victim);
        Assert.False(add.Combat.IsInCombat);
    }

    [Theory]
    [InlineData(SpectralSideAssault)]
    [InlineData(UnrelentingSideAssault)]
    public void SideAssault_AnIdleCreatureTheSpellReachesJoinsTheZoneFight(uint spell)
    {
        using var raid = new Raid([DummyEffect(spell, SpellImplicitTarget.Unit)]);
        Creature add = raid.Spawn(16127);
        Assert.False(add.Combat.IsInCombat);

        raid.Cast(raid.Tank, spell, add);

        Assert.True(add.Combat.IsInCombat);
    }

    [Fact]
    public void EveryLaneSpell_IsImplementedConsumedOrInert_ExactlyOnce()
    {
        HashSet<uint> scripted = [.. SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly).SpellIds];
        string consumers = File.ReadAllText(Path.Combine(RepositorySource.RequireRoot(), "src", "ArcaneCore.Playerbots", "Qa", "PlayerbotQaSpellConsumers.Naxxramas.cs"));
        HashSet<uint> consumed = [.. Regex.Matches(consumers, @"new\((\d+),").Select(m => uint.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];

        foreach (uint id in s_laneSpells)
        {
            int ways = (scripted.Contains(id) ? 1 : 0) + (consumed.Contains(id) ? 1 : 0) + (DeliberatelyInertSpells.TryGet(id, out _) ? 1 : 0);
            Assert.True(ways == 1, $"spell {id}: implemented/consumed/inert {ways} times, expected exactly one");
        }
    }
}

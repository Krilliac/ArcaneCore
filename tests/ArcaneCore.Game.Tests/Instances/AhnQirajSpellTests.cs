using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The Ahn'Qiraj spell lane: the SCRIPT_EFFECT and DUMMY aura spells of Viscidus, the C'Thun tentacles, the Twin Emperors and Captain Tuubid that
/// AhnQirajSpellScripts and RuinsSpellScripts implement, the glob rejoin the Viscidus AI performs, and the list of every spell of the lane.
/// </summary>
public sealed class AhnQirajSpellTests
{
    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo ScriptEffect(uint id) => Instant(Spell(id, Effect(SpellEffectName.ScriptEffect, 0, SpellImplicitTarget.UnitCaster)));

    private static SpellInfo Marker(uint id) =>
        Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
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
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    private static Creature MakeCreature(SpellTestKit kit, uint entry)
    {
        CreatureTemplate template = CreatureTestKit.Template(entry, t =>
        {
            t.MinLevelHealth = 10_000;
            t.MaxLevelHealth = 10_000;
        });
        var system = new CreatureMapSystem(kit.World.GetMap(0), CreatureTestKit.Content([template], [CreatureTestKit.Spawn(77, entry, 3, 0)]), null, random: new Random(1));
        kit.World.GetMap(0).AddUpdater(system);
        kit.World.RunTick(50);
        return Assert.Single(system.Creatures);
    }

    [Theory]
    [InlineData(10_000u, 10_000u, 20u)]
    [InlineData(5000u, 10_000u, 10u)]
    [InlineData(499u, 10_000u, 0u)]
    [InlineData(10_000u, 0u, 0u)]
    public void GlobCount_IsOnePerFivePercent(uint health, uint maxHealth, uint expected)
        => Assert.Equal(expected, ViscidusSummonGlobsScript.GlobCount(health, maxHealth));

    [Fact]
    public void SummonGlobs_ViscidusCastsOneGlobSpellPerFivePercentOfHealth()
    {
        SpellInfo[] globs = [.. Enumerable.Range(0, 20).Select(i => Marker(ViscidusSummonGlobsScript.FirstGlobSpell + (uint)i))];
        using SpellTestKit kit = Kit([ScriptEffect(25885), .. globs]);
        kit.AddPlayer(1);
        Creature viscidus = MakeCreature(kit, 15299);
        viscidus.Health = viscidus.MaxHealth / 2;
        uint expected = ViscidusSummonGlobsScript.GlobCount(viscidus.Health, viscidus.MaxHealth);
        Assert.Equal(10u, expected);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(viscidus, 25885, SpellCastTargets.ForSelf(), triggered: true));
        FlushSpellBatch(kit);

        for (uint i = 0; i < 20; i++)
        {
            Assert.Equal(i < expected, kit.System.HasAura(viscidus, ViscidusSummonGlobsScript.FirstGlobSpell + i));
        }
    }

    [Fact]
    public void SummonGlobs_IgnoresAnyoneButViscidus()
    {
        using SpellTestKit kit = Kit(ScriptEffect(25885), Marker(25865));
        kit.AddPlayer(1);
        Creature other = MakeCreature(kit, CreatureTestKit.WolfEntry);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(other, 25885, SpellCastTargets.ForSelf(), triggered: true));
        FlushSpellBatch(kit);

        Assert.False(kit.System.HasAura(other, 25865));
    }

    [Fact]
    public void DespawnGlobs_DespawnsAGlobAndSparesOtherCreatures()
    {
        using SpellTestKit globKit = Kit(ScriptEffect(26608));
        globKit.AddPlayer(1);
        Creature glob = MakeCreature(globKit, 15667);
        Assert.Equal(SpellCastResult.CastOk, globKit.System.CastSpell(glob, 26608, SpellCastTargets.ForSelf(), triggered: true));
        Assert.False(glob.IsAlive);

        using SpellTestKit otherKit = Kit(ScriptEffect(26608));
        otherKit.AddPlayer(1);
        Creature boss = MakeCreature(otherKit, 15299);
        Assert.Equal(SpellCastResult.CastOk, otherKit.System.CastSpell(boss, 26608, SpellCastTargets.ForSelf(), triggered: true));
        otherKit.World.RunTick(5000);
        Assert.True(boss.IsAlive);
    }

    [Theory]
    [InlineData(26217u, 26216u)]
    [InlineData(26237u, 26332u)]
    [InlineData(26398u, 26140u)]
    [InlineData(26767u, 26768u)]
    [InlineData(26584u, 26577u)]
    public void TentacleAndSlimeSummons_TheTargetCastsTheMatchingSummonOnItself(uint spell, uint summon)
    {
        using SpellTestKit kit = Kit(ScriptEffect(spell), Marker(summon));
        (Player player, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spell, SpellCastTargets.ForSelf(), triggered: true));
        FlushSpellBatch(kit);

        Assert.True(kit.System.HasAura(player, summon));
    }

    [Fact]
    public void AttackOrder_TheMarkedUnitCastsTheAreaTauntOnItself()
    {
        using SpellTestKit kit = Kit(Marker(25471), Marker(25473));
        (Player player, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 25471, SpellCastTargets.ForSelf(), triggered: true));
        FlushSpellBatch(kit);

        Assert.True(kit.System.HasAura(player, 25471));
        Assert.True(kit.System.HasAura(player, 25473));
    }

    [Fact]
    public void TwinTeleport_CastOnVeklorSwapsTheBrothers_AndVeknilashIgnoresIt()
    {
        using SpellTestKit kit = Kit(ScriptEffect(799), Marker(800), Marker(26638));
        Map map = kit.World.GetMap(531);
        map.AddUpdater(new TempleOfAhnQirajInstance(map));
        map.Combat.SpellMitigation = kit.System;
        var content = new CreatureContent([CreatureTestKit.Template(15275), CreatureTestKit.Template(15276)], [], [], [], []);
        var creatures = new CreatureMapSystem(map, content, random: new Random(1),
            aiServices: new CreatureAiServices { Spells = new SpellSystemCreatureCaster(kit.System), Hostility = new AlwaysHostile() });
        map.AddUpdater(creatures);
        Player player = TestWorld.CreatePlayer(1, 1, 0, new FakeSession(), 531);
        player.Relocate(1, 0, -104, 0, kit.Now);
        kit.World.AddPlayer(player);
        kit.World.RunTick(0);
        Creature nilash = creatures.SpawnTemporary(content.FindTemplate(15275)!, 3, 0, -104, 0);
        Creature lor = creatures.SpawnTemporary(content.FindTemplate(15276)!, 5, 0, -104, 0);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(nilash, 799, SpellCastTargets.ForSelf(), triggered: true));
        FlushSpellBatch(kit);
        Assert.InRange(nilash.X, 2.5f, 3.5f);
        Assert.InRange(lor.X, 4.5f, 5.5f);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(lor, 799, SpellCastTargets.ForSelf(), triggered: true));
        FlushSpellBatch(kit);
        Assert.InRange(nilash.X, 4.5f, 5.5f);
        Assert.InRange(lor.X, 2.5f, 3.5f);
    }

    private sealed class ViscidusArena : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public Map Map { get; }
        public CreatureMapSystem Creatures { get; }
        public FakeCaster Casts { get; } = new();
        public Player Tank { get; }
        public Creature Boss { get; }
        public ViscidusAI Ai => Assert.IsType<ViscidusAI>(Boss.AI);

        public ViscidusArena()
        {
            Map = World.GetMap(531);
            Map.AddUpdater(new TempleOfAhnQirajInstance(Map));
            var content = new CreatureContent(
                [CreatureTestKit.Template(15299, b => b.MinLevelHealth = b.MaxLevelHealth = 10_000), CreatureTestKit.Template(15667), CreatureTestKit.Template(15922)],
                [], [], [], []);
            Creatures = new CreatureMapSystem(Map, content, random: new Random(1),
                aiServices: new CreatureAiServices { Spells = Casts, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, 1, 0, new FakeSession(), 531);
            Tank.Relocate(1, 0, 450, 0, 0);
            World.AddPlayer(Tank);
            World.RunTick(0);
            Boss = Creatures.SpawnTemporary(content.FindTemplate(15299)!, 0, 0, 450, 0);
            Assert.True(Ai.AttackStart(Tank));
            Casts.Casts.Clear();
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void RejoinViscidus_ALastGlobArrivingCastsTheSpellAtTheBossAndEndsTheGlobPhase()
    {
        using var arena = new ViscidusArena();
        SpellInfo frost = Spell(900001, Effect(SpellEffectName.SchoolDamage, 1)) with { School = SpellSchool.Frost };
        for (int i = 0; i < 200; i++) arena.Ai.OnSpellHit(arena.Tank, frost);
        for (int i = 0; i < 150; i++)
            arena.Ai.OnMeleeHitReceived(new MeleeDamageInfo { Attacker = arena.Tank, Target = arena.Boss, Outcome = MeleeHitOutcome.Normal });
        Creature glob = arena.Creatures.SummonAt(arena.Boss, 15667, 40, 0, 450, 0, null, 60_000)!;
        arena.Ai.OnUpdate(2500);
        Assert.NotEqual(UnitFlags.None, arena.Boss.UnitFlags & UnitFlags.ImmuneToNpc);
        arena.Casts.Casts.Clear();

        glob.AI!.OnMovementInform(MovementGeneratorType.Point, 1);

        Assert.Contains(arena.Casts.Casts, c => c.Spell == 25896 && ReferenceEquals(c.Target, arena.Boss));
        Assert.Contains(arena.Casts.Casts, c => c.Spell == 25897);
        Assert.Equal(UnitFlags.None, arena.Boss.UnitFlags & UnitFlags.ImmuneToNpc);
    }

    [Fact]
    public void EveryLaneSpell_IsAScriptADummyAuraCaseAnInertEntryOrANamedConsumer()
    {
        uint[] scripted = [25885, 26608, 26217, 26237, 26398, 26767, 26584, 799];
        uint[] dummyAura = [25471];
        uint[] inert = [25926, 16245, 26235, 25171, 25148, 25711, 25830, 25833, 25834, 25835, 25844];
        uint[] consumed = [25896, 27934, 26205]; // QaSpellConsumers entries (PlayerbotQaSpellConsumers.AhnQiraj.cs); the QA registry tests check their needles

        HashSet<uint> ids = [.. SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly).SpellIds];
        foreach (uint id in scripted) Assert.Contains(id, ids);

        using var kit = new SpellTestKit();
        foreach (uint id in dummyAura) Assert.True(kit.System.HasDummyAuraHandler(id), $"spell {id}: no dummy aura case");
        foreach (uint id in inert) Assert.True(DeliberatelyInertSpells.TryGet(id, out _), $"spell {id}: not in the inert registry");
        foreach (uint id in scripted.Concat(dummyAura).Concat(consumed))
            Assert.False(DeliberatelyInertSpells.TryGet(id, out _), $"spell {id}: both handled and inert");
    }
}

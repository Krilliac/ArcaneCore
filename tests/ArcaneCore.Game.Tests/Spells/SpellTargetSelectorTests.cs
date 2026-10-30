using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The caster-relative implicit targets 48 (back), 49 (left) and 50 (right) and the script cone 60 (vmangos Spell.cpp:2986-3014 and :2702-2740),
/// with the Spell.dbc 5875 shapes of the content they unblock: Seeping Fog 24813/24814 (summon at 13 yd, target A 50/49), Moam's Summon Mana Fiend
/// 25682 (A 50), Blink 29209-29211 (leap, target B 48/49/50, 9 yd), Merithra's Wake 25145 (SCRIPT_EFFECT, target A 60).
/// </summary>
public sealed class SpellTargetSelectorTests
{
    private const uint SeepingFogRight = 924813;  // 24813: SUMMON_TOTEM-shaped here, target A 50, radius 13
    private const uint SeepingFogLeft = 924814;   // 24814: target A 49
    private const uint ManaFiend = 925682;        // 25682: target A 50
    private const uint Back = 920048;
    private const uint BlinkBack = 929209;        // 29209: LEAP, A caster, B 48, radius 9
    private const uint BlinkLeft = 929210;        // 29210: B 49
    private const uint BlinkRight = 929211;       // 29211: B 50
    private const uint BlinkForward = 921953;     // 1953: B 55
    private const uint MerithraWake = 925145;     // 25145: SCRIPT_EFFECT, A 60
    private const uint ConeDummy = 925146;        // a non-script effect on 60 (AOE damage targets)
    private const uint ConeDummyWithRows = 925147;
    private const uint ConeScriptWithRows = 925148;
    private const uint Gap59 = 920059;
    private const uint Gap48 = 920049;
    private const uint ListedEntry = 15500;
    private const uint UnlistedEntry = 15501;
    private const float Pi = MathF.PI;

    private sealed class Probe : ISpellCastObserver
    {
        public List<(int Effect, string Kind, uint Value)> Hits { get; } = [];

        public void OnUnsupported(SpellCast cast, int effectIndex, string kind, uint value) => Hits.Add((effectIndex, kind, value));
    }

    private static SpellImplicitTarget T(uint v) => (SpellImplicitTarget)v;

    private static SpellInfo Summon(uint id, uint target, float radius)
        => Spell(id, Effect(SpellEffectName.SummonTotem, 5, T(target)) with { Radius = radius }) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private static SpellInfo Blink(uint id, uint targetB)
        => Spell(id, Effect(SpellEffectName.Leap, 0, SpellImplicitTarget.UnitCaster, targetB: T(targetB)) with { Radius = 9f }) with
        { StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private static SpellInfo Cone(uint id, SpellEffectName effect)
        => Spell(id, Effect(effect, 0, T(60)) with { Radius = 30f }) with
        { StartRecoveryCategory = 0, StartRecoveryTime = 0, RangeIndex = 4, Range = new SpellRange(0, 30) };

    private static SpellTestKit Kit() => new(
        Summon(SeepingFogRight, 50, 13), Summon(SeepingFogLeft, 49, 13), Summon(ManaFiend, 50, 13), Summon(Back, 48, 4),
        Blink(BlinkBack, 48), Blink(BlinkLeft, 49), Blink(BlinkRight, 50), Blink(BlinkForward, 55),
        Cone(MerithraWake, SpellEffectName.ScriptEffect), Cone(ConeDummy, SpellEffectName.Dummy),
        Cone(ConeDummyWithRows, SpellEffectName.Dummy), Cone(ConeScriptWithRows, SpellEffectName.ScriptEffect),
        Spell(Gap59, Effect(SpellEffectName.Dummy, 0, T(59))), Spell(Gap48, Effect(SpellEffectName.Dummy, 0, T(48))));

    [Theory]
    [InlineData(SeepingFogRight, -0.5f, 13f)]
    [InlineData(SeepingFogLeft, 0.5f, 13f)]
    [InlineData(ManaFiend, -0.5f, 13f)]
    [InlineData(Back, 1.0f, 4f)]
    public void CasterRelativeSummon_LandsAtTheEffectRadius_AtTheVmangosAngle_AndReportsNothing(uint spell, float piMultiple, float radius)
    {
        using SpellTestKit kit = Kit();
        var probe = new Probe();
        kit.System.RegisterObserver(probe);
        var seen = new List<(float X, float Y, bool HasDest)>();
        kit.System.RegisterEffect(SpellEffectName.SummonTotem, ctx => seen.Add((ctx.Cast.Targets.Dest.X, ctx.Cast.Targets.Dest.Y, ctx.Cast.Targets.HasDest)));
        (Player caster, _) = kit.AddPlayer(1, 100, 200);
        caster.Orientation = Pi / 2; // north

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, spell, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Empty(probe.Hits);
        var hit = Assert.Single(seen);
        Assert.True(hit.HasDest);
        float angle = (Pi / 2) + (Pi * piMultiple);
        Assert.Equal(100 + (radius * MathF.Cos(angle)), hit.X, 3);
        Assert.Equal(200 + (radius * MathF.Sin(angle)), hit.Y, 3);
    }

    [Theory]
    [InlineData(BlinkBack, 91f, 100f)]
    [InlineData(BlinkLeft, 100f, 109f)]
    [InlineData(BlinkRight, 100f, 91f)]
    [InlineData(BlinkForward, 109f, 100f)]
    public void Blink_LeapsTheEffectRadiusInTheDirectionOfTargetB(uint spell, float x, float y)
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 100, 100);
        caster.Orientation = 0;

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, spell, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(x, caster.X, 2);
        Assert.Equal(y, caster.Y, 2);
    }

    private sealed class Cones : IDisposable
    {
        public Cones()
        {
            Kit = Kit();
            Kit.System.Relations = Relations;
            Kit.System.Store = new SpellStore(Kit.Store.All, [], [],
            [
                new SpellStore.ScriptTarget(ConeDummyWithRows, 1, ListedEntry, 0),
                new SpellStore.ScriptTarget(ConeScriptWithRows, 1, ListedEntry, 0),
            ]);
            Map map = Kit.World.GetMap(0);
            Creatures = new CreatureMapSystem(map, Content([Template(ListedEntry), Template(UnlistedEntry)], []),
                new CreatureOptions { AggroRate = 0, RespawnPacifyMs = 0 }, random: new Random(1));
            map.AddUpdater(Creatures);
            (Caster, _) = Kit.AddPlayer(1, 0, 0);
            Caster.Orientation = 0;
            Kit.System.RegisterEffect(SpellEffectName.ScriptEffect, ctx => Hit.Add(ctx.Target));
            Kit.System.RegisterEffect(SpellEffectName.Dummy, ctx => Hit.Add(ctx.Target));
        }

        public SpellTestKit Kit { get; }
        public FakeRelations Relations { get; } = new();
        public CreatureMapSystem Creatures { get; }
        public Player Caster { get; }
        public List<Unit> Hit { get; } = [];

        public Creature Spawn(uint entry, float x, float y, bool hostile = true)
        {
            Creature creature = Creatures.SpawnTemporary(Creatures.Content.FindTemplate(entry)!, x, y, 83.5f, 0);
            if (hostile)
                Relations.Hostile.Add(creature.Guid);
            return creature;
        }

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void ScriptCone_WithRows_HitsOnlyTheListedUnitsInsideTheConeInFront_AndNeverTheCaster()
    {
        using var f = new Cones();
        Creature listedAhead = f.Spawn(ListedEntry, 6, 1);
        f.Spawn(UnlistedEntry, 6, -1);          // in the cone, not listed
        f.Spawn(ListedEntry, -6, 0);            // listed, behind the caster
        f.Spawn(ListedEntry, 0, 8);             // listed, beside the caster (outside the 90 degree cone)
        f.Spawn(ListedEntry, 60, 0);            // listed, beyond the radius

        Assert.Equal(SpellCastResult.CastOk, f.Kit.System.CastSpell(f.Caster, ConeScriptWithRows, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Same(listedAhead, Assert.Single(f.Hit));
    }

    [Fact]
    public void ScriptCone_NonScriptEffectWithRows_StillFiltersByTheRows()
    {
        using var f = new Cones();
        Creature listedAhead = f.Spawn(ListedEntry, 6, 1, hostile: false);
        f.Spawn(UnlistedEntry, 6, -1);

        f.Kit.System.CastSpell(f.Caster, ConeDummyWithRows, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Same(listedAhead, Assert.Single(f.Hit));
    }

    [Fact]
    public void ScriptCone_ScriptEffectWithoutRows_TakesEveryLivingUnitInTheCone()
    {
        using var f = new Cones();
        Creature enemy = f.Spawn(UnlistedEntry, 6, 1);
        Creature friend = f.Spawn(ListedEntry, 7, -1, hostile: false);
        f.Spawn(ListedEntry, -6, 0);

        f.Kit.System.CastSpell(f.Caster, MerithraWake, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(2, f.Hit.Count);
        Assert.Contains(enemy, f.Hit);
        Assert.Contains(friend, f.Hit);
    }

    [Fact]
    public void ScriptCone_OtherEffectWithoutRows_TakesOnlyTheEnemiesInTheCone()
    {
        using var f = new Cones();
        Creature enemy = f.Spawn(UnlistedEntry, 6, 1);
        f.Spawn(ListedEntry, 7, -1, hostile: false);

        f.Kit.System.CastSpell(f.Caster, ConeDummy, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Same(enemy, Assert.Single(f.Hit));
    }

    [Fact]
    public void ScriptCone_ReportsNothingUnsupported()
    {
        using var f = new Cones();
        var probe = new Probe();
        f.Kit.System.RegisterObserver(probe);

        f.Kit.System.CastSpell(f.Caster, MerithraWake, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Empty(probe.Hits);
    }

    [Fact]
    public void CanSelectImplicitTarget_ListsTarget59AsAGap_AndTarget48AsNone()
    {
        using SpellTestKit kit = Kit();

        Assert.False(kit.System.CanSelectImplicitTarget(kit.Store.Get(Gap59)!, kit.Store.Get(Gap59)!.Effects[0]));
        Assert.True(kit.System.CanSelectImplicitTarget(kit.Store.Get(Gap48)!, kit.Store.Get(Gap48)!.Effects[0]));
    }

    /// <summary>The static answer must equal what a cast does: for every target value, "cannot select" is exactly "the cast reports an implicit target as unsupported".</summary>
    [Fact]
    public void CanSelectImplicitTarget_AgreesWithTheRuntimeReport_ForEveryTargetValue()
    {
        var spells = new List<SpellInfo>();
        for (uint target = 0; target <= 80; target++)
        {
            spells.Add(Spell(930000 + target, Effect(SpellEffectName.Dummy, 0, T(target))) with
            {
                StartRecoveryCategory = 0, StartRecoveryTime = 0, RangeIndex = 4, Range = new SpellRange(0, 30),
            });
        }

        using var kit = new SpellTestKit([.. spells]);
        var probe = new Probe();
        kit.System.RegisterObserver(probe);
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player other, _) = kit.AddPlayer(2, 3, 0);
        var disagreements = new List<uint>();
        for (uint target = 0; target <= 80; target++)
        {
            SpellInfo spell = kit.Store.Get(930000 + target)!;
            probe.Hits.Clear();
            kit.System.CastSpell(caster, spell.Id, SpellCastTargets.ForUnit(other.Guid), triggered: true);
            bool reported = probe.Hits.Any(h => h.Kind == "implicit target");
            if (reported == kit.System.CanSelectImplicitTarget(spell, spell.Effects[0]))
                disagreements.Add(target);
        }

        Assert.Empty(disagreements);
    }
}

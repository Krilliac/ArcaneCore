using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Game.Tests.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Researched;

/// <summary>
/// The Naxxramas spells the G7 research settled from cmangos SpellScripts (boss_maexxna.cpp, boss_gothik.cpp,
/// boss_anubrekhan.cpp). Each test observes the follow-up casts through <see cref="SpellSystem.SpellHit"/>.
/// </summary>
public sealed class NaxxramasResearchedScriptTests
{
    private static readonly uint[] MessageSpells = [29280, 29281, 29282, 29283, 29285, 29287];

    private static SpellInfo Instant(SpellInfo spell) => spell with
    {
        StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1, RangeIndex = 4, Range = new SpellRange(0, 100),
    };

    private static SpellInfo Marker(uint id, SpellImplicitTarget target) =>
        Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, target, AuraType.Dummy)) with { Duration = new SpellDuration(30_000, 0, 30_000) });

    private static SpellTestKit Kit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        kit.System.Units = new MapObjectResolver();
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    private static List<(Unit Caster, Unit Target, uint Spell)> Record(SpellTestKit kit)
    {
        var hits = new List<(Unit, Unit, uint)>();
        kit.System.SpellHit += (caster, target, spell) => hits.Add((caster, target, spell.Id));
        return hits;
    }

    [Theory]
    [InlineData(15f, 28618u)]
    [InlineData(20f, 28618u)]
    [InlineData(25f, 28619u)]
    [InlineData(35f, 28620u)]
    [InlineData(50f, 28621u)]
    public void WebWrapMessage_TheWrappedPlayerPullsByDistance(float distance, uint pull)
    {
        using SpellTestKit kit = Kit(
            Instant(Spell(29281, Effect(SpellEffectName.ScriptEffect, 0, SpellImplicitTarget.UnitEnemy))),
            Marker(28618, SpellImplicitTarget.UnitEnemy), Marker(28619, SpellImplicitTarget.UnitEnemy),
            Marker(28620, SpellImplicitTarget.UnitEnemy), Marker(28621, SpellImplicitTarget.UnitEnemy));
        (Player trigger, _) = kit.AddPlayer(1);
        (Player wrapped, _) = kit.AddPlayer(2, distance, 0);
        kit.System.Relations = new FakeRelations { Hostile = { wrapped.Guid } };
        List<(Unit Caster, Unit Target, uint Spell)> hits = Record(kit);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(trigger, 29281, SpellCastTargets.ForUnit(wrapped.Guid), triggered: true));

        (Unit caster, Unit target, uint spell) = Assert.Single(hits, h => h.Spell is >= 28618 and <= 28621);
        Assert.Same(wrapped, caster);
        Assert.Same(trigger, target);
        Assert.Equal(pull, spell);
    }

    [Fact]
    public void WebWrapMaexxna_ThreeNonTankPlayersEachCastADistinctMessage()
    {
        var spells = new List<SpellInfo> { Instant(Spell(28673, Effect(SpellEffectName.ScriptEffect, 0, SpellImplicitTarget.UnitCaster))) };
        spells.AddRange(MessageSpells.Select(id => Instant(Spell(id, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitCaster)))));
        using SpellTestKit kit = Kit([.. spells]);
        var players = new List<Player>();
        for (uint i = 1; i <= 5; i++)
        {
            players.Add(kit.AddPlayer(i, i, 0).Player);
        }

        Map map = kit.World.GetMap(0);
        var maexxna = new CombatTestUnit();
        maexxna.Relocate(0, 0, players[0].Z, 0, 0);
        map.AddObject(maexxna);
        map.Combat.Track(maexxna);
        for (int i = 0; i < players.Count; i++)
        {
            maexxna.Combat.Threat.AddThreat(players[i], 1000 - (i * 100)); // players[0] holds the top of the list
        }

        List<(Unit Caster, Unit Target, uint Spell)> hits = Record(kit);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(maexxna, 28673, SpellCastTargets.ForSelf(), triggered: true));

        List<(Unit Caster, Unit Target, uint Spell)> messages = [.. hits.Where(h => MessageSpells.Contains(h.Spell))];
        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.Same(m.Caster, m.Target));
        Assert.DoesNotContain(messages, m => ReferenceEquals(m.Caster, players[0]));
        Assert.Equal(3, messages.Select(m => m.Caster).Distinct().Count());
        Assert.Equal(3, messages.Select(m => m.Spell).Distinct().Count());
    }

    [Fact]
    public void WebWrapMaexxna_WithOnlyTheTankOnTheList_WrapsNobody()
    {
        var spells = new List<SpellInfo> { Instant(Spell(28673, Effect(SpellEffectName.ScriptEffect, 0, SpellImplicitTarget.UnitCaster))) };
        spells.AddRange(MessageSpells.Select(id => Instant(Spell(id, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitCaster)))));
        using SpellTestKit kit = Kit([.. spells]);
        (Player tank, _) = kit.AddPlayer(1, 2, 0);
        Map map = kit.World.GetMap(0);
        var maexxna = new CombatTestUnit();
        maexxna.Relocate(0, 0, tank.Z, 0, 0);
        map.AddObject(maexxna);
        map.Combat.Track(maexxna);
        maexxna.Combat.Threat.AddThreat(tank, 1000);
        List<(Unit Caster, Unit Target, uint Spell)> hits = Record(kit);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(maexxna, 28673, SpellCastTargets.ForSelf(), triggered: true));

        Assert.DoesNotContain(hits, h => MessageSpells.Contains(h.Spell));
    }

    [Theory]
    [InlineData(27896u, 27915u)]
    [InlineData(27930u, 27931u)]
    [InlineData(27938u, 27937u)]
    public void ChooseRandomSkullPile_AnchorsExactlyOneTarget(uint choose, uint anchor)
    {
        using SpellTestKit kit = Kit(
            Instant(Spell(choose, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.LocationCasterSrc, targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 30f })),
            Marker(anchor, SpellImplicitTarget.UnitEnemy));
        (Player gothik, _) = kit.AddPlayer(1);
        (Player first, _) = kit.AddPlayer(2, 5, 0);
        (Player second, _) = kit.AddPlayer(3, -5, 0);
        (Player third, _) = kit.AddPlayer(4, 0, 5);
        kit.System.Relations = new FakeRelations { Hostile = { first.Guid, second.Guid, third.Guid } };
        List<(Unit Caster, Unit Target, uint Spell)> hits = Record(kit);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(gothik, choose, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Single(hits, h => h.Spell == choose);
        (Unit caster, Unit target, _) = Assert.Single(hits, h => h.Spell == anchor);
        Assert.Same(gothik, caster);
        Assert.Contains(target, new Unit[] { first, second, third });
    }

    [Fact]
    public void SummonCorpseScarabs_ACorpseCastsTheSummon_ALivingTargetDoesNot()
    {
        using SpellTestKit kit = Kit(
            Instant(Spell(28961, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)) with { AttributesEx2 = SpellAttributesEx2.AllowDeadTarget }),
            Instant(Spell(28864, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitCaster)) with { Attributes = SpellAttributes.AllowCastWhileDead }));
        (Player anub, _) = kit.AddPlayer(1);
        (Player corpse, _) = kit.AddPlayer(2, 5, 0);
        (Player living, _) = kit.AddPlayer(3, -5, 0);
        kit.System.Relations = new FakeRelations { Hostile = { corpse.Guid, living.Guid } };
        corpse.Map!.FindUpdater<MapCombat>()!.Kill(anub, corpse);
        kit.System.OnUnitDied(corpse);
        Assert.False(corpse.IsAlive);
        List<(Unit Caster, Unit Target, uint Spell)> hits = Record(kit);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(anub, 28961, SpellCastTargets.ForUnit(living.Guid), triggered: true));
        Assert.DoesNotContain(hits, h => h.Spell == 28864);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(anub, 28961, SpellCastTargets.ForUnit(corpse.Guid), triggered: true));
        (Unit caster, _, _) = Assert.Single(hits, h => h.Spell == 28864);
        Assert.Same(corpse, caster);
    }
}

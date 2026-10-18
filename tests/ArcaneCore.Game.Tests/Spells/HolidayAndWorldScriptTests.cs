using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>vmangos Spell::EffectDummy / EffectScriptEffect holiday and world cases ported to <c>Spells/Generic/HolidayAndWorldScripts.cs</c>.</summary>
public sealed class HolidayAndWorldScriptTests
{
    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo Self(uint id, SpellEffectName effect = SpellEffectName.ScriptEffect) => Instant(Spell(id, Effect(effect, 0, SpellImplicitTarget.UnitCaster)));

    private static SpellInfo AtUnit(uint id, SpellEffectName effect = SpellEffectName.ScriptEffect) => Instant(Spell(id, Effect(effect, 0, SpellImplicitTarget.Unit)) with
    {
        RangeIndex = 4,
        Range = new SpellRange(0, 40),
    });

    private static SpellInfo Marker(uint id) => Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
    {
        Duration = new SpellDuration(30_000, 0, 30_000),
    });

    private static SpellTestKit Kit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    [Fact]
    public void Tribute_PicksTheRaceBuff()
    {
        Assert.Equal(24105u, TributeScript.BuffFor(24194, Race.Human));
        Assert.Equal(24101u, TributeScript.BuffFor(24195, Race.Troll));
        Assert.Equal(0u, TributeScript.BuffFor(24194, Race.Orc));

        using SpellTestKit kit = Kit(Self(24194), Marker(24106));
        (Player gnome, _) = kit.AddPlayer(1, race: Race.Gnome);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(gnome, 24194, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(gnome, 24106));
    }

    [Fact]
    public void PyrewoodTransformation_PairsEveryVillager()
    {
        foreach ((uint human, uint worgen) in new (uint, uint)[] { (1891, 1892), (1894, 1893), (1895, 1896), (3528, 3529), (3530, 3531), (3532, 3533) })
        {
            Assert.Equal(worgen, PyrewoodTransformationScript.PairOf(human));
            Assert.Equal(human, PyrewoodTransformationScript.PairOf(worgen));
        }

        Assert.Equal(0u, PyrewoodTransformationScript.PairOf(1));
    }

    [Fact]
    public void CoarseDynamite_ThrowsOneOfTheThreeBlasts()
    {
        using SpellTestKit kit = Kit(Self(9009, SpellEffectName.Dummy), Marker(9002), Marker(9003), Marker(9004));
        (Player thrower, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(thrower, 9009, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(1, new uint[] { 9002, 9003, 9004 }.Count(id => kit.System.HasAura(thrower, id)));
    }

    [Fact]
    public void FriendshipBracelet_MendsTheHeartbroken()
    {
        using SpellTestKit kit = Kit(AtUnit(26899, SpellEffectName.Dummy), Marker(26898), Marker(26921), Marker(26664));
        (Player giver, _) = kit.AddPlayer(1);
        (Player heartbroken, _) = kit.AddPlayer(2, 3, 0);
        Assert.True(kit.System.AddAura(heartbroken, 26898));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(giver, 26899, SpellCastTargets.ForUnit(heartbroken.Guid), triggered: true));

        Assert.False(kit.System.HasAura(heartbroken, 26898));
        Assert.True(kit.System.HasAura(heartbroken, 26921));
        Assert.True(kit.System.HasAura(giver, 26664));
    }

    [Fact]
    public void RemoveAmorous_And_MagicWings_RemoveTheirAuras()
    {
        using SpellTestKit kit = Kit(Self(26879, SpellEffectName.Dummy), Self(24742), Marker(26869), Marker(24754));
        (Player player, _) = kit.AddPlayer(1);
        Assert.True(kit.System.AddAura(player, 26869));
        Assert.True(kit.System.AddAura(player, 24754));

        kit.System.CastSpell(player, 26879, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, 24742, SpellCastTargets.ForSelf(), triggered: true);

        Assert.False(kit.System.HasAura(player, 26869));
        Assert.False(kit.System.HasAura(player, 24754));
    }

    [Fact]
    public void BagOfCandies_GivesOneCandy()
    {
        uint[] candies = [26668, 26670, 26671, 26672, 26673, 26674, 26675, 26676];
        using SpellTestKit kit = Kit([Self(26678), .. candies.Select(Marker)]);
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, 26678, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(1, candies.Count(id => kit.System.HasAura(player, id)));
    }

    [Fact]
    public void SymbolOfLife_StandsUpHenzeFaulk()
    {
        using var kit = new PetTestKit([AtUnit(FaulkAI.SymbolOfLife, SpellEffectName.Dummy)], creatureSpells: true, extraTemplates:
        [
            CreatureTestSupport.Template(FaulkAI.HenzeEntry, b =>
            {
                b.MinLevel = 10;
                b.MaxLevel = 10;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
                b.Faction = 12;
            }),
        ]);
        SpellScriptDispatcher.Install(kit.Spells.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player paladin, _) = kit.AddPlayer(1);
        Creature henze = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(FaulkAI.HenzeEntry)!, 3, 0, paladin.Z, 0);
        kit.Run(200);
        var ai = Assert.IsType<FaulkAI>(henze.AI);
        Assert.Equal(StandState.Dead, henze.StandState);

        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastSpell(paladin, FaulkAI.SymbolOfLife, SpellCastTargets.ForUnit(henze.Guid), triggered: true));
        kit.Run(200);

        Assert.True(ai.IsRevived);
        Assert.Equal(StandState.Stand, henze.StandState);
    }

    [Fact]
    public void Discovery_RegistersTheBatch()
    {
        var ids = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly).SpellIds.ToHashSet();
        foreach (uint id in new uint[] { 7671, 8593, 9009, 17190, 16032, 17512, 24194, 24195, 24731, 24742, 26678, 26899, 26879, 27662, 31929, 32061, 22972, 22985 })
        {
            Assert.Contains(id, ids);
        }
    }
}
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Kernel.WorldData.WorldState;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The outbound leg of the necropolis communique, spell 28373 (<see cref="ScourgeInvasionCatalog.CommuniqueNecropolisToProxies"/>), through the real
/// spell system. Its dummy effect has implicit targets 18 / 8 (TARGET_ENUM_UNITS_SCRIPT_AOE_AT_DEST_LOC) and spell_script_target rows listing the
/// Necropolis Proxy. <see cref="SpellSystem"/> selects script-listed units only for targets 38 and 7 (SpellSystem.ScriptTargets.cs), so target 8
/// selects nothing: the control test proves the harness does find the proxies with a supported script target, and the gap test pins the
/// engine gap until target 8 is implemented (then it must be inverted into a positive test).
/// </summary>
public sealed class ScourgeNecropolisCommuniqueTargetTests
{
    private const uint Proxy = 16398;
    private const uint ScriptAoeAtDestLoc = 8;

    private static (PetTestKit Kit, List<Unit> Hit, Player Caster, Creature Near, Creature Other, Creature Unlisted) Setup(
        SpellImplicitTarget targetA, SpellImplicitTarget targetB)
    {
        var kit = new PetTestKit(
            [
                Spell(ScourgeInvasionCatalog.CommuniqueNecropolisToProxies, Effect(SpellEffectName.Dummy, 1, targetA, targetB: targetB)) with
                {
                    RangeIndex = 4,
                    Range = new SpellRange(0, 200),
                },
            ],
            extraTemplates: [CreatureTestSupport.Template(Proxy, b => b.Faction = 35)]);
        kit.Spells.System.Store = new SpellStore(kit.Spells.Store.All, [], [],
            [new SpellStore.ScriptTarget(ScourgeInvasionCatalog.CommuniqueNecropolisToProxies, 1, Proxy, 0)]);
        List<Unit> hit = [];
        kit.Spells.System.SpellHit += (_, target, spell) =>
        {
            if (spell.Id == ScourgeInvasionCatalog.CommuniqueNecropolisToProxies) hit.Add(target);
        };
        (Player caster, _) = kit.AddPlayer(1);
        Creature near = kit.Creatures.SummonForInstance(Proxy, 10f, 0f, 0f, 0f)!;
        Creature other = kit.Creatures.SummonForInstance(Proxy, 0f, 20f, 0f, 0f)!;
        Creature unlisted = kit.Creatures.SummonForInstance(PetTestKit.WildEntry, 5f, 0f, 0f, 0f)!;
        return (kit, hit, caster, near, other, unlisted);
    }

    [Fact]
    public void ASupportedScriptTargetSelectsTheNearestListedProxyAndNotAnUnlistedCreature()
    {
        (PetTestKit kit, List<Unit> hit, Player caster, Creature near, Creature other, Creature unlisted) =
            Setup(SpellImplicitTarget.UnitScriptNearCaster, SpellImplicitTarget.None);
        using (kit)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, ScourgeInvasionCatalog.CommuniqueNecropolisToProxies));

            Assert.Same(near, Assert.Single(hit));
            Assert.DoesNotContain(other, hit);
            Assert.DoesNotContain(unlisted, hit);
        }
    }

    [Fact]
    public void TargetEightOfTheRealCommuniqueSelectsNoProxyTodayEngineGap()
    {
        (PetTestKit kit, List<Unit> hit, Player caster, _, _, _) =
            Setup(SpellImplicitTarget.LocationCasterDest, (SpellImplicitTarget)ScriptAoeAtDestLoc);
        using (kit)
        {
            Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, ScourgeInvasionCatalog.CommuniqueNecropolisToProxies));

            // UNVERIFIED-in-engine / GAP: TARGET_ENUM_UNITS_SCRIPT_AOE_AT_DEST_LOC (8) has no selector, so the 28373 hit never reaches a proxy's AI.
            Assert.Empty(hit);
        }
    }
}

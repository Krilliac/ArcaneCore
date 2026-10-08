using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Shaman;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// Mana Tide (vmangos scripts/spells/spell_shaman.cpp:49-67; mangos-classic SpellAuras.cpp:1209-1213): a PERIODIC_TRIGGER_SPELL Mana Tide aura makes
/// its target cast the trigger spell on itself with the aura's amount (170, the build 5875 Mana Tide value) instead of the trigger's own. The aura is the
/// party area aura the totem carries (APPLY_AREA_AURA_PARTY, 20 yards, a tick every 3 s, 12 s here). In the trigger shape the script serves, the trigger
/// is a mana ENERGIZE whose own value is 1, so the amount shows whose value was used. The build 5875 row itself (classic-db z2815) is a PERIODIC_ENERGIZE
/// of 170 in the same area aura; the last test pins that it restores the same mana to the same units without the script.
/// </summary>
public sealed class ManaTideScriptTests
{
    private const uint ManaTideEnergize = 990_811;
    private const int ManaTideAmount = 170;

    private sealed class Rig : IDisposable
    {
        public Rig(bool triggerShape)
        {
            SpellEffectInfo effect = triggerShape
                ? Effect(SpellEffectName.ApplyAreaAuraParty, ManaTideAmount, aura: AuraType.PeriodicTriggerSpell, amplitude: 3000, trigger: ManaTideEnergize)
                : Effect(SpellEffectName.ApplyAreaAuraParty, ManaTideAmount, aura: AuraType.PeriodicEnergize, amplitude: 3000, misc: (int)PowerType.Mana);
            Kit = new SpellTestKit(
                Spell(ManaTideModule.ManaTide, effect with { Radius = 20f }) with
                {
                    School = SpellSchool.Nature,
                    SpellFamilyName = 11,
                    Duration = new SpellDuration(12_000, 0, 12_000),
                    StartRecoveryCategory = 0,
                    StartRecoveryTime = 0,
                },
                Spell(ManaTideEnergize, Effect(SpellEffectName.Energize, 1, misc: (int)PowerType.Mana)) with
                {
                    StartRecoveryCategory = 0,
                    StartRecoveryTime = 0,
                });
            (Shaman, _) = Kit.AddPlayer(1);
            (Member, _) = Kit.AddPlayer(2, 5, 0);
            (Stranger, _) = Kit.AddPlayer(3, 0, 5);
            var groups = new FakeGroups();
            groups.Parties.Add([Shaman.Guid, Member.Guid]);
            Kit.System.Groups = groups;
            foreach (Player player in new[] { Shaman, Member, Stranger })
            {
                player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana, 5000);
                SpellSystem.SetPower(player, PowerType.Mana, 0);
            }
        }

        public SpellTestKit Kit { get; }

        public Player Shaman { get; }

        public Player Member { get; }

        public Player Stranger { get; }

        public void Dispose() => Kit.Dispose();
    }

    private static uint Mana(Unit unit) => SpellSystem.GetPower(unit, PowerType.Mana);

    private static void RunTheTotem(Rig rig)
    {
        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Shaman, ManaTideModule.ManaTide, SpellCastTargets.ForSelf(), triggered: true));
        rig.Kit.Advance(200);                                       // the area aura reaches the member
        Assert.True(rig.Kit.System.HasAura(rig.Member, ManaTideModule.ManaTide));
        Assert.False(rig.Kit.System.HasAura(rig.Stranger, ManaTideModule.ManaTide));
    }

    [Fact]
    public void EveryTick_EachPartyMemberInRange_RestoresTheAurasAmount_ToItself()
    {
        using var rig = new Rig(triggerShape: true);
        RunTheTotem(rig);

        rig.Kit.Advance(3000);

        Assert.Equal((uint)ManaTideAmount, Mana(rig.Shaman));    // the aura's 170, not the energize's own 1
        Assert.Equal((uint)ManaTideAmount, Mana(rig.Member));
        Assert.Equal(0u, Mana(rig.Stranger));                    // not in the party

        rig.Kit.Advance(9000);                                    // the rest of the 12 seconds: the source ticks four times in all
        Assert.Equal((uint)(4 * ManaTideAmount), Mana(rig.Shaman));
        AssertWholeTicks(Mana(rig.Member));                      // every tick of the member's copy is the aura's 170 too
        Assert.Equal(0u, Mana(rig.Stranger));
    }

    /// <summary>
    /// The member's copy of the area aura is applied on the update after the source and ends with it, so it ticks three or four times; each tick is
    /// the aura's amount (the area aura timing belongs to the aura engine, not to this script).
    /// </summary>
    private static void AssertWholeTicks(uint mana)
    {
        Assert.Equal(0u, mana % ManaTideAmount);
        Assert.InRange(mana / ManaTideAmount, 3u, 4u);
    }

    [Fact]
    public void TheBuild5875Row_APeriodicEnergize_RestoresTheSameManaToTheSameUnits()
    {
        using var rig = new Rig(triggerShape: false);
        RunTheTotem(rig);

        rig.Kit.Advance(12_000);

        Assert.Equal((uint)(4 * ManaTideAmount), Mana(rig.Shaman));
        AssertWholeTicks(Mana(rig.Member));
        Assert.Equal(0u, Mana(rig.Stranger));
    }
}

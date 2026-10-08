using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Rogue;

/// <summary>
/// Ruthlessness (14157) and Seal Fate (14189) add a combo point from a proc of the cast that is running. vmangos defers that cast past the end
/// of the current spell ("Need add combopoint AFTER finishing move (or they get dropped in finish phase)", UnitAuraProcHandler.cpp:1592-1615),
/// because Spell::finish clears a finisher's points (Spell.cpp:4374-4395). Without the deferral the Ruthlessness point is wiped by the very
/// finisher that granted it.
/// </summary>
public sealed class ComboPointProcDeferralTests
{
    private const uint Eviscerate = 940_101;
    private const uint SinisterStrike = 940_102;
    private const uint Ruthlessness = 14156;
    private const uint RuthlessnessPoint = 14157;
    private const uint SealFate = 14186;
    private const uint SealFatePoint = 14189;

    private const uint FamilyRogue = 8;
    private const ulong EviscerateFlag = 0x20000;
    private const ulong SinisterStrikeFlag = 0x2;
    private const uint FinishingDamage = 0x00100000;
    private const uint AlwaysHit = 0x00040000;

    private static SpellInfo Ability(uint id, ulong flag, params SpellEffectInfo[] effects) => Spell(id, effects) with
    {
        SpellFamilyName = FamilyRogue,
        SpellFamilyFlags = flag,
        DamageClass = SpellDamageClass.Melee,
        AttributesEx3 = AlwaysHit,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo PointTalent(uint id, uint trigger, ulong classMask) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ProcTriggerSpell, trigger: trigger) with { ItemType = (uint)classMask }) with
        {
            SpellFamilyName = FamilyRogue,
            Duration = new SpellDuration(-1, 0, -1),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            ProcFlags = ProcFlags.DealMeleeAbility,
            ProcChance = 100,
        };

    private static SpellInfo AddPoint(uint id) => Spell(id, Effect(SpellEffectName.AddComboPoints, 1, SpellImplicitTarget.UnitEnemy)) with
    {
        SpellFamilyName = FamilyRogue,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Kit = new SpellTestKit(
                Ability(Eviscerate, EviscerateFlag, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
                {
                    AttributesEx = (SpellAttributesEx)FinishingDamage,
                },
                Ability(SinisterStrike, SinisterStrikeFlag,
                    Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy),
                    Effect(SpellEffectName.AddComboPoints, 1, SpellImplicitTarget.UnitEnemy)),
                PointTalent(Ruthlessness, RuthlessnessPoint, EviscerateFlag),
                AddPoint(RuthlessnessPoint),
                PointTalent(SealFate, SealFatePoint, SinisterStrikeFlag),
                AddPoint(SealFatePoint));
            (Rogue, _) = Kit.AddPlayer(1, race: Race.Human);
            Rogue.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Rogue);
            Rogue.Flags |= PlayerFlags.PvpDesired;
            MapCombat.UpdatePvp(Rogue, true);
            (Enemy, _) = Kit.AddPlayer(2, 3, 0, race: Race.Orc);
            Enemy.Flags |= PlayerFlags.PvpDesired;
            MapCombat.UpdatePvp(Enemy, true);
            Enemy.Health = Enemy.MaxHealth = 5000;
            Combos = new ComboPointService(Kit.System, (_, guid) => Kit.World.FindOnlinePlayer(guid));
            Combos.Install();
            Kit.World.RunTick(0);
        }

        public SpellTestKit Kit { get; }

        public Player Rogue { get; }

        public Player Enemy { get; }

        public ComboPointService Combos { get; }

        public void Learn(uint talent)
        {
            Kit.System.CastSpell(Rogue, talent, SpellCastTargets.ForSelf(), triggered: true);
            Kit.Now++;
        }

        public SpellCastResult Cast(uint spell) => Kit.System.CastSpell(Rogue, spell, SpellCastTargets.ForUnit(Enemy.Guid), triggered: false);

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void Ruthlessness_ThePointItGrantsSurvivesTheFinisherThatGrantedIt()
    {
        using var rig = new Rig();
        rig.Learn(Ruthlessness);
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 3);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(Eviscerate));

        // Spell::finish dropped the three points; the deferred 14157 then added one on the finisher's target.
        Assert.Equal(1, rig.Combos.GetComboPoints(rig.Rogue));
        Assert.Equal(rig.Enemy.Guid, rig.Combos.GetComboTarget(rig.Rogue));
        Assert.Equal(1, rig.Rogue.GetByte(UpdateFields.PlayerFieldBytes, 1));
    }

    [Fact]
    public void TheComboPointSpell_CastOutsideAnyCastOfTheRogue_AddsAtOnce()
    {
        using var rig = new Rig();
        rig.Learn(Ruthlessness);
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 2);

        // Without a finisher there is nothing to clear: a proc outside any cast of the rogue adds at once, as vmangos does with a proc delay.
        rig.Kit.System.CastSpell(rig.Rogue, RuthlessnessPoint, SpellCastTargets.ForUnit(rig.Enemy.Guid), triggered: true);

        Assert.Equal(3, rig.Combos.GetComboPoints(rig.Rogue));
    }

    [Fact]
    public void SealFate_ItsExtraPointIsAddedAfterTheBuildersOwn()
    {
        using var rig = new Rig();
        rig.Learn(SealFate);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(SinisterStrike));

        Assert.Equal(2, rig.Combos.GetComboPoints(rig.Rogue)); // the builder's point, then Seal Fate's 14189 once the cast ended
    }
}

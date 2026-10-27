using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Procs;
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

    /// <summary>
    /// With spell batching on (<c>ARCANECORE_TEST_SPELL_BATCH_MS</c>) a cast that goes off is held until the next window boundary: advance
    /// the clock to it so the held go resolves. A no-op with the window at 0.
    /// </summary>
    private static void FlushSpellBatch(SpellTestKit kit)
    {
        uint window = kit.System.SpellBatchWindowMs;
        if (window != 0)
        {
            kit.Advance(window - (kit.Now % window));
        }
    }

    private sealed class Rig : IDisposable
    {
        /// <param name="deferral">Install the combo point service (which turns the post-finish deferral on), or, off, only its point effect.</param>
        public Rig(bool deferral = true)
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
            if (deferral)
            {
                Combos.Install();
            }
            else
            {
                Kit.System.RegisterEffect(SpellEffectName.AddComboPoints, context => Combos.AddComboPoints((Player)context.Caster, context.Target, context.Value));
            }

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

        /// <summary>An Eviscerate hit reported to the proc engine outside any cast of the rogue (a proc that arrives after the cast is gone).</summary>
        public void ProcEviscerateHit() => Kit.System.ProcDamageAndSpell(Rogue, new ProcEvent
        {
            Victim = Enemy,
            AttackerFlags = ProcFlags.DealMeleeAbility,
            Extra = ProcFlagsEx.NormalHit,
            Amount = 10,
            OriginalAmount = 10,
            ProcSpell = Kit.Store.Get(Eviscerate),
        });

        public SpellCastResult Cast(uint spell)
        {
            SpellCastResult result = Kit.System.CastSpell(Rogue, spell, SpellCastTargets.ForUnit(Enemy.Guid), triggered: false);
            FlushSpellBatch(Kit);
            return result;
        }

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
    public void TheComboPointSpellItself_CastDirectly_AddsAtOnce()
    {
        using var rig = new Rig();
        rig.Learn(Ruthlessness);
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 2);

        // 14157 cast by itself, not through the Ruthlessness proc: the deferral belongs to the proc handler, so the spell adds its point at once.
        rig.Kit.System.CastSpell(rig.Rogue, RuthlessnessPoint, SpellCastTargets.ForUnit(rig.Enemy.Guid), triggered: true);

        Assert.Equal(3, rig.Combos.GetComboPoints(rig.Rogue));
    }

    [Fact]
    public void Ruthlessness_AProcWithNoRunningCast_AddsNoPoint()
    {
        using var rig = new Rig();
        rig.Learn(Ruthlessness);
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 2);

        // vmangos takes the point's target from GetCurrentSpell(CURRENT_GENERIC_SPELL) and fails the proc without one (UnitAuraProcHandler.cpp:1592-1615).
        rig.ProcEviscerateHit();

        Assert.Equal(2, rig.Combos.GetComboPoints(rig.Rogue));
    }

    [Fact]
    public void Ruthlessness_WithTheDeferralOff_ThePointIsCastAtOnce()
    {
        using var rig = new Rig(deferral: false);
        rig.Learn(Ruthlessness);
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 2);

        // Nothing promised to run the post-finish procs, so the handler casts 14157 on the proc's target immediately (the same event as above).
        rig.ProcEviscerateHit();

        Assert.Equal(3, rig.Combos.GetComboPoints(rig.Rogue));
        Assert.Equal(rig.Enemy.Guid, rig.Combos.GetComboTarget(rig.Rogue));
    }

    [Fact]
    public void SealFate_ItsExtraPointIsAddedAfterTheBuildersOwn()
    {
        using var rig = new Rig();
        rig.Learn(SealFate);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(SinisterStrike));

        Assert.Equal(2, rig.Combos.GetComboPoints(rig.Rogue)); // the builder's point, then Seal Fate's 14189 once the cast ended
    }

    // --- spell batching ------------------------------------------------------------------------------------------

    /// <summary>
    /// Spell batching on (SpellSystem.Batching.cs): the instant is held and leaves its cast slot, so when the batch resolves it, the procs must
    /// still find it as the running cast to take their point's target from, or the deferred point is lost.
    /// </summary>
    private static void CastHeldThenResolve(Rig rig, uint spell, int pointsWhileHeld)
    {
        rig.Kit.System.SpellBatchWindowMs = 400;
        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Rogue, spell, SpellCastTargets.ForUnit(rig.Enemy.Guid), triggered: false));
        Assert.Equal(pointsWhileHeld, rig.Combos.GetComboPoints(rig.Rogue)); // held: nothing happened yet

        rig.Kit.Advance(400);
    }

    [Fact]
    public void SpellBatching_Ruthlessness_ThePointOfAHeldFinisherStillLands()
    {
        using var rig = new Rig();
        rig.Learn(Ruthlessness);
        rig.Combos.AddComboPoints(rig.Rogue, rig.Enemy, 3);

        CastHeldThenResolve(rig, Eviscerate, pointsWhileHeld: 3);

        Assert.Equal(1, rig.Combos.GetComboPoints(rig.Rogue));
        Assert.Equal(rig.Enemy.Guid, rig.Combos.GetComboTarget(rig.Rogue));
    }

    [Fact]
    public void SpellBatching_SealFate_TheExtraPointOfAHeldBuilderStillLands()
    {
        using var rig = new Rig();
        rig.Learn(SealFate);

        CastHeldThenResolve(rig, SinisterStrike, pointsWhileHeld: 0);

        Assert.Equal(2, rig.Combos.GetComboPoints(rig.Rogue));
    }
}

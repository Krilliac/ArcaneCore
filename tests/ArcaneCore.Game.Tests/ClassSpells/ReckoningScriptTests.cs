using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Paladin;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Procs;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// Reckoning (vmangos scripts/spells/spell_paladin.cpp:178-206). Build 5875 shapes (classic-db z2815): the talent 20177 is a PROC_TRIGGER_SPELL aura of
/// 20178 procced by taken hits (procFlags 0x222A8; spell_proc_event procEx CRITICAL_HIT; the 20% chance is 100 here), and 20178 is ADD_EXTRA_ATTACKS 1
/// on the paladin. Each Reckoning adds one extra attack to those pending, up to 4; the pending attacks are swung on the next melee update.
/// </summary>
public sealed class ReckoningScriptTests : IDisposable
{
    private readonly SpellTestKit _kit;
    private readonly Player _paladin;
    private readonly Player _enemy;

    public ReckoningScriptTests()
    {
        _kit = new SpellTestKit(
            Spell(ReckoningScript.ReckoningTalent, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ProcTriggerSpell, trigger: ReckoningScript.Reckoning)) with
            {
                Attributes = SpellAttributes.Passive,
                Duration = new SpellDuration(-1, 0, -1),
                ProcFlags = (ProcFlags)0x222A8,
                ProcChance = 100,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(ReckoningScript.Reckoning, Effect(SpellEffectName.AddExtraAttacks, 1)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        (_paladin, _) = _kit.AddPlayer(1);
        (_enemy, _) = _kit.AddPlayer(2, 2, 0, race: Race.Orc); // the other faction: a valid auto-attack target
        _kit.System.Relations = new FakeRelations { Hostile = { _enemy.Guid } };
        _kit.System.ProcEvents = new ProcCatalog(new SpellProcEventRecord(ReckoningScript.ReckoningTalent, 0, 0, 0, 0, 0, 0, (uint)ProcFlagsEx.CriticalHit, 0, 0, 0));
        SpellScriptDispatcher.Install(_kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
    }

    public void Dispose() => _kit.Dispose();

    private void Reckon() => Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_paladin, ReckoningScript.Reckoning, SpellCastTargets.ForSelf(), triggered: true));

    private static MeleeDamageInfo Hit(Unit attacker, Unit victim, MeleeHitOutcome outcome) => new()
    {
        Attacker = attacker,
        Target = victim,
        AttackType = WeaponAttackType.BaseAttack,
        Outcome = outcome,
        HitInfo = HitInfo.AffectsVictim,
        TargetState = VictimState.Normal,
        TotalDamage = 10,
    };

    [Fact]
    public void EachReckoning_AddsOneExtraAttack_UpToFour()
    {
        Reckon();
        Assert.Equal(1u, _paladin.Combat.ExtraAttacks);
        Reckon();
        Reckon();
        Assert.Equal(3u, _paladin.Combat.ExtraAttacks);
        Reckon();
        Reckon();
        Reckon();
        Assert.Equal(ReckoningScript.MaxStack, _paladin.Combat.ExtraAttacks);
    }

    [Fact]
    public void CritsTakenThroughTheTalent_StackTheExtraAttacks()
    {
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_paladin, ReckoningScript.ReckoningTalent, SpellCastTargets.ForSelf(), triggered: true));
        _kit.Advance(100);

        _kit.System.OnMeleeSwingResolved(Hit(_enemy, _paladin, MeleeHitOutcome.Normal)); // procEx CRITICAL_HIT: a normal hit is not enough
        Assert.Equal(0u, _paladin.Combat.ExtraAttacks);
        _kit.System.OnMeleeSwingResolved(Hit(_enemy, _paladin, MeleeHitOutcome.Crit));
        _kit.System.OnMeleeSwingResolved(Hit(_enemy, _paladin, MeleeHitOutcome.Crit));

        Assert.Equal(2u, _paladin.Combat.ExtraAttacks);
    }

    [Fact]
    public void ThePendingAttacks_AreSwungOnTheNextMeleeUpdate_AndConsumed()
    {
        Map map = _kit.World.GetMap(0);
        _enemy.Map!.Combat.TogglePvp(_enemy, true);
        _paladin.SetFloat(UpdateFields.UnitFieldMindamage, 5);
        _paladin.SetFloat(UpdateFields.UnitFieldMaxdamage, 5);
        Reckon();
        Reckon();
        Reckon();
        Assert.True(map.Combat.Attack(_paladin, _enemy));
        _paladin.Combat.SetAttackTimer(WeaponAttackType.BaseAttack, 2000); // the ordinary swing is not due
        int swings = 0;
        map.Combat.MeleeSwingResolved += info => swings += ReferenceEquals(info.Attacker, _paladin) ? 1 : 0;

        map.Combat.Update(0);

        Assert.Equal(3, swings);
        Assert.Equal(0u, _paladin.Combat.ExtraAttacks);
    }
}

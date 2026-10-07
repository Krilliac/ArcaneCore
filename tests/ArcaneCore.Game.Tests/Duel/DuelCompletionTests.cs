using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Duel.DuelRig;

namespace ArcaneCore.Game.Tests.Duel;

/// <summary>vmangos Player::DuelComplete (Player.cpp:6726-6822): what a finished duel removes and resets.</summary>
public sealed class DuelCompletionTests
{
    [Theory]
    [InlineData(DuelCompleteType.Won, true)]
    [InlineData(DuelCompleteType.Fled, true)]
    [InlineData(DuelCompleteType.Interrupted, false)]
    public void Completion_ResetsExtraAttacksOnlyForADecidedDuel(DuelCompleteType type, bool clears)
    {
        using var rig = new DuelRig();
        rig.Challenge();
        rig.AcceptAndStart();
        Assert.True(rig.A.Combat.QueueExtraAttacks(2));
        Assert.True(rig.B.Combat.QueueExtraAttacks(3));

        rig.Service.Complete(rig.A, type);

        Assert.Equal(clears ? 0u : 2u, rig.A.Combat.ExtraAttacks);
        Assert.Equal(clears ? 0u : 3u, rig.B.Combat.ExtraAttacks);
    }

    private static bool Has(DuelRig rig, Player target, uint spellId) => rig.Kit.System.GetAuras(target).Any(h => h.Spell.Id == spellId);

    private static void Cast(DuelRig rig, Unit caster, Player target, uint spellId)
        => rig.Kit.System.CastSpell(caster, spellId, SpellCastTargets.ForUnit(target.Guid), triggered: true);

    [Fact]
    public void Completion_RemovesTheNegativeAurasEachSideCastSinceTheStart_AndKeepsTheRest()
    {
        using var rig = new DuelRig();
        Player third = rig.Kit.AddPlayer(3, 14, 10).Player;
        rig.Challenge();
        Cast(rig, rig.B, rig.A, DebuffA);          // before the start: kept
        Cast(rig, rig.B, rig.A, Buff);             // positive opponent buff must be applied before duel hostility starts
        rig.Now += 5;
        rig.AcceptAndStart();                      // start at Now + 3
        long start = rig.A.Duel!.StartTimeSeconds;
        Cast(rig, rig.B, rig.A, DebuffB);          // applied at the start second: removed
        Cast(rig, rig.A, rig.B, DebuffA);          // the other side: removed
        Cast(rig, third, rig.A, 109);              // third party debuff (the stun): kept

        rig.Service.Complete(rig.A, DuelCompleteType.Won);

        Assert.True(start > 0);
        Assert.True(Has(rig, rig.A, DebuffA), "applied before the start");
        Assert.False(Has(rig, rig.A, DebuffB));
        Assert.False(Has(rig, rig.B, DebuffA));
        Assert.True(Has(rig, rig.A, Buff));
        Assert.True(Has(rig, rig.A, 109));
    }

    [Fact]
    public void Completion_AlsoRemovesAReflectedDebuff_ThatItsOwnCasterNowCarries()
    {
        // vmangos Player.cpp:6762-6768, 6781-6787 (> 1.6.1): "You are no longer able to kill players in duels with reflected DoT spells".
        using var rig = new DuelRig();
        rig.Kit.System.CombatRules = new ReflectOnlyRules();
        rig.Kit.System.CastSpell(rig.A, ReflectAura, SpellCastTargets.ForSelf(), triggered: true);
        rig.Challenge();
        rig.AcceptAndStart();
        Cast(rig, rig.B, rig.A, ReflectableDebuff);
        SpellAuraHolder reflected = Assert.Single(rig.Kit.System.GetAuras(rig.B), h => h.Spell.Id == ReflectableDebuff);
        Assert.True(reflected.IsReflected);
        Assert.Equal(rig.B.Guid, reflected.CasterGuid); // its own caster: the "cast by the opponent" rule alone would keep it
        Assert.False(Has(rig, rig.A, ReflectableDebuff));

        rig.Service.Complete(rig.A, DuelCompleteType.Won);

        Assert.False(Has(rig, rig.B, ReflectableDebuff));
    }

    /// <summary>Every spell lands unless the vanilla reflect step turns it back.</summary>
    private sealed class ReflectOnlyRules : VanillaSpellCombatRules
    {
        public override SpellMissInfo RollHit(SpellSystem system, Unit caster, Unit target, SpellInfo spell)
            => system.RollSpellReflect(caster, target, spell) ? SpellMissInfo.Reflect : SpellMissInfo.None;
    }

    [Fact]
    public void AnAuraAppliedInTheSecondBeforeTheStart_IsKept_AndInTheSameSecond_IsRemoved()
    {
        using var rig = new DuelRig();
        rig.Challenge();
        rig.Service.Accept(rig.B);
        rig.Now += 2;
        Cast(rig, rig.B, rig.A, DebuffA);          // second start-1
        rig.Now += 1;
        rig.Tick();                                // starts at this second
        Cast(rig, rig.B, rig.A, DebuffB);          // same second as the start

        rig.Service.Complete(rig.A, DuelCompleteType.Won);

        Assert.True(Has(rig, rig.A, DebuffA));
        Assert.False(Has(rig, rig.A, DebuffB));
    }

    [Fact]
    public void ACancelledRequest_RemovesEveryHostileAuraTheOtherSideCast_AsVmangosDoes()
    {
        using var rig = new DuelRig();
        Cast(rig, rig.B, rig.A, DebuffA);          // long before any duel
        rig.Now += 100;
        rig.Challenge();

        rig.Service.Cancel(rig.B);                 // startTime is 0, so "applied since the start" is everything (Player.cpp:6757-6770)

        Assert.False(Has(rig, rig.A, DebuffA));
    }

    [Fact]
    public void Completion_ClearsComboPointsAimedAtTheOtherPlayer_ButNotAtAnyoneElse()
    {
        using var rig = new DuelRig();
        var combos = new ComboPointService(rig.Kit.System, (_, guid) => rig.Map.Combat.FindUnit(guid));
        rig.Service.Combos = combos;
        Player third = rig.Kit.AddPlayer(3, 14, 10).Player;
        rig.Challenge();
        rig.AcceptAndStart();
        combos.AddComboPoints(rig.A, rig.B, 3);
        combos.AddComboPoints(rig.B, third, 2);

        rig.Service.Complete(rig.A, DuelCompleteType.Won);

        Assert.Equal(0, combos.GetComboPoints(rig.A));
        Assert.Equal(2, combos.GetComboPoints(rig.B));
    }

    [Fact]
    public void Completion_AlsoClearsTheOpponentsPointsOnTheLoser()
    {
        using var rig = new DuelRig();
        var combos = new ComboPointService(rig.Kit.System, (_, guid) => rig.Map.Combat.FindUnit(guid));
        rig.Service.Combos = combos;
        rig.Challenge();
        rig.AcceptAndStart();
        combos.AddComboPoints(rig.B, rig.A, 4);

        rig.Service.Complete(rig.A, DuelCompleteType.Won);

        Assert.Equal(0, combos.GetComboPoints(rig.B));
    }

    [Fact]
    public void Completion_ResetsArbiterAndTeamOnBoth_AndTheFieldsReachObservers()
    {
        using var rig = new DuelRig();
        rig.Challenge();
        rig.AcceptAndStart();
        Assert.NotEqual(0ul, rig.B.DuelArbiter);

        rig.Service.Complete(rig.B, DuelCompleteType.Won);

        Assert.Equal(0ul, rig.A.DuelArbiter);
        Assert.Equal(0ul, rig.B.DuelArbiter);
        Assert.Equal(0u, rig.A.DuelTeam);
        Assert.Equal(0u, rig.B.DuelTeam);
    }
}

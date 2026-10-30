using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Spell batching (SpellSystem.Batching.cs, <c>SpellRules:SpellBatchWindowMs</c>): a player's cast that goes off waits for the window boundary
/// and then resolves with the other casts of the window (Kaivax, Blizzard, 2019, https://us.forums.blizzard.com/en/wow/t/137118/1: casters who
/// act on each other in one batch both get their action; 1.13.6 used a 400 ms window, https://us.forums.blizzard.com/en/wow/t/838945).
/// </summary>
public sealed class SpellBatchingTests
{
    private const uint Zap = 941000;      // instant 15 fire damage
    private const uint Slay = 941001;     // instant damage far above a test player's health
    private const uint Kick = 941002;     // instant interrupt with a 4 s school lockout
    private const uint SlowBolt = 941003; // 1.5 s silenceable fire bolt
    private const uint Jab = 941004;      // instant, off the global cooldown, 10 s cooldown
    private const uint Window = 400;

    [Fact]
    public void WithoutAWindow_AnInstantCastResolvesAtOnce()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddHostilePlayer(2, 10);
        kit.Spellbook.Teach(caster, Zap);

        Assert.Equal(0u, new ArcaneCore.Game.Spells.Rules.SpellRuleOptions().SpellBatchWindowMs);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Zap, SpellCastTargets.ForUnit(target.Guid)));

        Assert.Equal(0, kit.System.PendingBatchedGoes);
        Assert.True(target.Health < 60);
    }

    [Fact]
    public void WithAWindow_AnInstantCastIsHeldUntilTheBoundary_ThenGoesOff()
    {
        using var kit = Kit(Window);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, FakeSession targetSession) = kit.AddHostilePlayer(2, 10);
        kit.Spellbook.Teach(caster, Zap);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Zap, SpellCastTargets.ForUnit(target.Guid)));
        Assert.Equal(1, kit.System.PendingBatchedGoes);
        Assert.True(kit.System.HasPendingGo(caster));
        Assert.False(kit.System.HasPendingGo(target));
        Assert.Equal(60u, target.Health);
        Assert.Empty(Packets(targetSession, WorldOpcode.SmsgSpellGo));

        kit.Advance(Window - 100);
        Assert.Equal(60u, target.Health);

        kit.Advance(100);
        Assert.Equal(0, kit.System.PendingBatchedGoes);
        Assert.False(kit.System.HasPendingGo(caster));
        Assert.True(target.Health < 60);
        Assert.Single(Packets(targetSession, WorldOpcode.SmsgSpellGo));
    }

    [Fact]
    public void TwoPlayersWhoKillEachOtherInOneWindow_BothDie()
    {
        using var kit = Kit(Window);
        (Player first, _) = kit.AddPlayer(1);
        (Player second, _) = kit.AddHostilePlayer(2, 10);
        FlagForPvp(first); // the second casts back at the first: opposing teams, both PvP flagged
        kit.Spellbook.Teach(first, Slay);
        kit.Spellbook.Teach(second, Slay);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(first, Slay, SpellCastTargets.ForUnit(second.Guid)));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(second, Slay, SpellCastTargets.ForUnit(first.Guid)));
        kit.Advance(Window);

        Assert.False(first.IsAlive);
        Assert.False(second.IsAlive);
    }

    [Fact]
    public void WithoutAWindow_TheFirstKillStopsTheSecondCast()
    {
        using var kit = Kit();
        (Player first, _) = kit.AddPlayer(1);
        (Player second, _) = kit.AddHostilePlayer(2, 10);
        FlagForPvp(first); // the second would be allowed to cast back, if it were still alive
        kit.Spellbook.Teach(first, Slay);
        kit.Spellbook.Teach(second, Slay);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(first, Slay, SpellCastTargets.ForUnit(second.Guid)));
        Assert.NotEqual(SpellCastResult.CastOk, kit.System.HandleCastRequest(second, Slay, SpellCastTargets.ForUnit(first.Guid)));

        Assert.True(first.IsAlive);
        Assert.False(second.IsAlive);
    }

    [Fact]
    public void AStunInTheSameWindow_DoesNotStopTheStunnedPlayersCast()
    {
        using var kit = Kit(Window);
        (Player stunner, _) = kit.AddPlayer(1);
        (Player stunned, _) = kit.AddHostilePlayer(2, 10);
        FlagForPvp(stunner); // the stunned player casts back at the stunner
        kit.Spellbook.Teach(stunner, StunSpell);
        kit.Spellbook.Teach(stunned, Zap);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(stunner, StunSpell, SpellCastTargets.ForUnit(stunned.Guid)));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(stunned, Zap, SpellCastTargets.ForUnit(stunner.Guid)));
        kit.Advance(Window);

        Assert.True((stunned.UnitFlags & UnitFlags.Stunned) != 0);
        Assert.True(stunner.Health < 60);
    }

    [Fact]
    public void AFinishedCastBar_HeldInTheWindow_IsNotInterruptedByAKickInTheSameWindow()
    {
        using var kit = Kit(Window);
        (Player kicker, _) = kit.AddPlayer(1);
        (Player caster, _) = kit.AddHostilePlayer(2, 10);
        FlagForPvp(kicker); // the caster aims its bolt at the kicker
        kit.Spellbook.Teach(caster, SlowBolt);
        kit.Spellbook.Teach(kicker, Kick);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, SlowBolt, SpellCastTargets.ForUnit(kicker.Guid)));
        kit.Advance(1500); // the bar is done at 1500; the boundaries fall at 400, 800, 1200 and 1600
        SpellCast held = Assert.IsType<SpellCast>(kit.System.GetState(caster.Guid)!.CurrentCast); // still reads as casting until its go
        Assert.Equal(SpellCastState.Preparing, held.State);
        Assert.Equal(1, kit.System.PendingBatchedGoes);
        kit.System.CancelCast(caster, SlowBolt); // too late to cancel: it is committed
        Assert.Same(held, kit.System.GetState(caster.Guid)!.CurrentCast);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(kicker, Kick, SpellCastTargets.ForUnit(caster.Guid)));
        kit.Advance(100);

        Assert.True(kicker.Health < 60);
        Assert.False(kit.System.IsSchoolLocked(caster, SpellSchool.Fire));
        Assert.Null(kit.System.GetState(caster.Guid)?.CurrentCast);
    }

    [Fact]
    public void AnOffGlobalCooldownSpell_SentTwiceInOneWindow_GoesOffOnce()
    {
        using var kit = Kit(Window);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddHostilePlayer(2, 10);
        kit.Spellbook.Teach(caster, Jab);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Jab, SpellCastTargets.ForUnit(target.Guid)));
        Assert.Equal(SpellCastResult.NotReady, kit.System.HandleCastRequest(caster, Jab, SpellCastTargets.ForUnit(target.Guid)));
        Assert.Equal(1, kit.System.PendingBatchedGoes);
        kit.Advance(Window);

        Assert.Equal(45u, target.Health); // one 15-point hit
        Assert.Equal(SpellCastResult.NotReady, kit.System.HandleCastRequest(caster, Jab, SpellCastTargets.ForUnit(target.Guid))); // its cooldown now runs
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ACreatureCast_IsHeldToo_UnlessCreatureBatchingIsOff(bool batchCreatures, bool held)
    {
        using var kit = Kit(Window);
        kit.System.SpellBatchCreatures = batchCreatures;
        (Player target, _) = kit.AddPlayer(1);
        Creature caster = Creature(kit, 9001, 5);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Zap, SpellCastTargets.ForUnit(target.Guid), triggered: false));

        Assert.Equal(held, kit.System.HasPendingGo(caster));
        Assert.Equal(held, target.Health == 60);
        kit.Advance(Window);
        Assert.True(target.Health < 60);
    }

    [Fact]
    public void AnAuraThatRunsOut_StaysUntilTheNextBatch_ThenGoes()
    {
        using var kit = Kit(Window); // the clock starts at 10 000, a boundary
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10);
        kit.System.CastSpell(caster, StunSpell, SpellCastTargets.ForUnit(target.Guid), triggered: true); // 4 s, lands at once

        kit.Advance(4000); // runs out at 14 000, in the batch that started there
        Assert.True(kit.System.HasAura(target, StunSpell));
        kit.Advance(Window - 100);
        Assert.True(kit.System.HasAura(target, StunSpell));
        Assert.True((target.UnitFlags & UnitFlags.Stunned) != 0);

        kit.Advance(100); // the first update of the next batch
        Assert.False(kit.System.HasAura(target, StunSpell));
        Assert.True((target.UnitFlags & UnitFlags.Stunned) == 0);
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(Window, false)]
    public void WithoutAWindow_OrWithAuraExpiryBatchingOff_AnAuraGoesWhenItRunsOut(uint window, bool batchAuraExpiry)
    {
        using var kit = Kit(window);
        kit.System.SpellBatchAuraExpiry = batchAuraExpiry;
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10);
        kit.System.CastSpell(caster, StunSpell, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        kit.Advance(3900);
        Assert.True(kit.System.HasAura(target, StunSpell));
        kit.Advance(100);
        Assert.False(kit.System.HasAura(target, StunSpell));
    }

    [Fact]
    public void ACastThatFailsItsCheck_IsNotHeld()
    {
        using var kit = Kit(Window);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddHostilePlayer(2, 100);
        kit.Spellbook.Teach(caster, Zap);

        Assert.Equal(SpellCastResult.OutOfRange, kit.System.HandleCastRequest(caster, Zap, SpellCastTargets.ForUnit(target.Guid)));

        Assert.Equal(0, kit.System.PendingBatchedGoes);
    }

    [Fact]
    public void SwitchingTheWindowOff_ReleasesTheHeldCastsOnTheNextUpdate()
    {
        using var kit = Kit(Window);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddHostilePlayer(2, 10);
        kit.Spellbook.Teach(caster, Zap);
        kit.System.HandleCastRequest(caster, Zap, SpellCastTargets.ForUnit(target.Guid));

        kit.System.SpellBatchWindowMs = 0;
        kit.Advance(100);

        Assert.Equal(0, kit.System.PendingBatchedGoes);
        Assert.True(target.Health < 60);
    }

    [Fact]
    public void ACasterWhoLeavesTheWorldBeforeTheBoundary_HisHeldCastEndsWithoutEffect()
    {
        using var kit = Kit(Window);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddHostilePlayer(2, 10);
        kit.Spellbook.Teach(caster, Zap);
        kit.System.HandleCastRequest(caster, Zap, SpellCastTargets.ForUnit(target.Guid));

        kit.World.RemovePlayer(caster);
        kit.Advance(Window);

        Assert.Equal(0, kit.System.PendingBatchedGoes);
        Assert.Equal(60u, target.Health);
    }

    private static Creature Creature(SpellTestKit kit, uint guid, float x)
    {
        var template = Template(guid, b => b.Faction = 14);
        var creature = new Creature(guid, template, null, Content([template], []), new Random(1));
        creature.MapId = 0;
        creature.Relocate(x, 0, 83.5f, 0, kit.Now);
        var map = kit.World.GetMap(0);
        map.AddObject(creature);
        map.Combat.Track(creature);
        return creature;
    }

    private sealed class BatchProbeFault(string message) : Exception(message);

    [Fact]
    public void AThrowingBatchedCast_RaisesTheSpellBatchFaultCountByExactlyOne()
    {
        using var kit = Kit(Window);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddHostilePlayer(2, 10);
        kit.Spellbook.Teach(caster, Zap);
        kit.System.RegisterEffect(SpellEffectName.SchoolDamage, c => throw new BatchProbeFault("spell-batch-probe-1"));
        string type = typeof(BatchProbeFault).FullName!;
        long Count(string site) => FaultLedger.Shared.Snapshot().Where(s => s.Site == site && s.ExceptionType == type).Sum(s => s.Count);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Zap, SpellCastTargets.ForUnit(target.Guid)));
        Assert.Equal(1, kit.System.PendingBatchedGoes);
        Assert.Equal(0, Count("spell.batch"));

        kit.Advance(Window);

        Assert.Equal(1, Count("spell.batch"));
        Assert.Equal(60u, target.Health);
        Assert.Equal(0, kit.System.PendingBatchedGoes);
    }
    private static SpellTestKit Kit(uint window = 0)
    {
        static SpellInfo Ranged(SpellInfo spell) => spell with { RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 };
        var kit = new SpellTestKit(
            Ranged(Spell(Zap, Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy))
                with { School = SpellSchool.Fire, DamageClass = SpellDamageClass.Magic }),
            Ranged(Spell(Slay, Effect(SpellEffectName.SchoolDamage, 5000, SpellImplicitTarget.UnitEnemy))
                with { School = SpellSchool.Fire, DamageClass = SpellDamageClass.Magic }),
            Ranged(Spell(Jab, Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy))
                with { School = SpellSchool.Fire, DamageClass = SpellDamageClass.Magic, RecoveryTime = 10_000 }),
            Ranged(Spell(Kick, Effect(SpellEffectName.InterruptCast, 0, SpellImplicitTarget.UnitEnemy)) with { Duration = new SpellDuration(4000, 0, 4000) }),
            Ranged(Spell(SlowBolt, Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy))
                with { School = SpellSchool.Fire, CastTime = new SpellCastTime(1500, 0, 0), PreventionType = 1, DamageClass = SpellDamageClass.Magic }));
        kit.System.SpellBatchWindowMs = window;
        return kit;
    }
}

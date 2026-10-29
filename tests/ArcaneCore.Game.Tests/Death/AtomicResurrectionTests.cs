using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Death.Ghost;
using ArcaneCore.Game.Death.Resurrection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// MapCombat.ResurrectPlayer must be atomic (vmangos Player::ResurrectPlayer, Player.cpp ~4641, runs to the end): a hook that throws
/// (<see cref="CombatHooks.RemoveGhostForm"/>, <see cref="CombatHooks.OnResurrected"/>) must not leave a player Alive with
/// PLAYER_FLAGS_GHOST, the ghost aura, a corpse in the world and 1 health. The ghost form is the real 8326 aura here (spell shaped like
/// classic-db's row, as in <see cref="GhostFormTests"/>), so "no ghost aura" is a meaningful assertion.
/// </summary>
public sealed class AtomicResurrectionTests
{
    private const long T = 1_700_000_000;
    private const uint Resurrection = 10881;
    private const uint SlowResurrection = 10882; // same effect with a 2 s cast bar

    public enum Throwing
    {
        RemoveGhostForm,
        OnResurrected,
    }

    private static SpellInfo Res(uint id, int castTimeMs) => SpellTestKit.Spell(id,
        SpellTestKit.Effect(SpellEffectName.ResurrectNew, 500, SpellImplicitTarget.None, misc: 750)) with
    {
        Targets = (uint)SpellCastTargetFlags.CorpseAlly,
        CastTime = new SpellCastTime(castTimeMs, 0, castTimeMs),
    };

    private static SpellInfo GhostAura(uint id, params SpellEffectInfo[] effects) => SpellTestKit.Spell(id, effects) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        Attributes = (SpellAttributes)763363584, // classic-db: CASTABLE_WHILE_DEAD
        AttributesEx3 = 0x100000,                // ALLOW_AURA_WHILE_DEAD
    };

    private static readonly SpellInfo GhostSpell = GhostAura(CombatConstants.GhostSpellId,
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Ghost),
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModIncreaseSpeed),
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 25, aura: AuraType.ModIncreaseSwimSpeed));

    private static readonly SpellInfo WispSpell = GhostAura(CombatConstants.WispGhostSpellId,
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModIncreaseSpeed),
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModIncreaseSwimSpeed));

    private sealed class FaultTestException(string message) : Exception(message);

    private sealed class ThrowingHooks(Throwing which, Func<Exception> make) : CombatHooks
    {
        public override void RemoveGhostForm(Player player)
        {
            if (which == Throwing.RemoveGhostForm)
            {
                throw make();
            }

            base.RemoveGhostForm(player);
        }

        public override void OnResurrected(Player player, bool applySickness)
        {
            if (which == Throwing.OnResurrected)
            {
                throw make();
            }

            base.OnResurrected(player, applySickness);
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Kit = new SpellTestKit(Res(Resurrection, 0), Res(SlowResurrection, 2000), GhostSpell, WispSpell);
            DeathHooks.Register(Kit.World, new DeathHooks(new DeathOptions { GhostFormAura = true }, new FixedDeathClock(T)));
            DeathSeams.Of(Kit.World).TryRegisterGhostForm(new GhostForm(Kit.World, () => Kit.System));
            Teleports = new TeleportService(Kit.World, _ => { }, _ => { });
            Service = new ResurrectionService(Kit.World, () => Teleports);
            Kit.System.Resurrection = Service;
            CasterSession = new FakeSession(1);
            Caster = CombatTestKit.AddPlayer(Kit.World, 1, 0, 0, CasterSession);
            GhostSession = new FakeSession(2);
            Ghost = CombatTestKit.AddPlayer(Kit.World, 2, 50, 0, GhostSession);
            Kit.World.RunTick(1);
            Kit.World.GetMap(0).Combat.SpellMitigation = Kit.System; // what SpellFeature does in production (SpellFeature.SubscribeCombat)
            Ghost.Health = 0;
            Combat.KillPlayer(Ghost);
            Assert.True(Combat.RepopPlayer(Ghost)); // a ghost at its corpse, wearing the real ghost aura
            CombatTestKit.AckPendingMovement(Ghost);
            Kit.World.RunTick(1);
            GhostSession.Clear();
            CasterSession.Clear();
            Ghost.Combat.GhostTime = T - 100_000; // the reclaim delay is long over
            Assert.True(Kit.System.HasAura(Ghost, CombatConstants.GhostSpellId));
            Assert.NotEqual(0u, (uint)(Ghost.Flags & PlayerFlags.Ghost));
            Assert.NotNull(Ghost.Combat.Corpse);
        }

        public SpellTestKit Kit { get; }

        public TeleportService Teleports { get; }

        public ResurrectionService Service { get; }

        public FakeSession CasterSession { get; }

        public FakeSession GhostSession { get; }

        public Player Caster { get; }

        public Player Ghost { get; }

        public MapCombat Combat => Kit.World.GetMap(0).Combat;

        public void Throw(Throwing which, Func<Exception>? make = null)
            => Combat.Hooks = new ThrowingHooks(which, make ?? (() => new NullReferenceException("hook fault")));

        public void CastAtCorpse(Unit caster, uint spell, bool withUnit = false)
        {
            var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.CorpseAlly, Corpse = Ghost.Combat.Corpse!.Guid };
            if (withUnit)
            {
                targets.Mask |= SpellCastTargetFlags.Unit;
                targets.Unit = Ghost.Guid;
            }

            Assert.Equal(SpellCastResult.CastOk, Kit.System.CastSpell(caster, spell, targets, triggered: spell == Resurrection));
        }

        /// <summary>The whole resurrection, not half of it: alive, no ghost flag or aura, no corpse, restored vitals.</summary>
        public void AssertFullyAlive(Player player)
        {
            Assert.False(player.Combat.DeathState == DeathState.Alive && (player.Flags & PlayerFlags.Ghost) != 0, "Alive with PLAYER_FLAGS_GHOST: the half-state");
            Assert.Equal(DeathState.Alive, player.Combat.DeathState);
            Assert.Equal(0u, (uint)(player.Flags & PlayerFlags.Ghost));
            Assert.False(Kit.System.HasAura(player, CombatConstants.GhostSpellId), "ghost aura 8326 still on");
            Assert.Null(player.Combat.Corpse);
            Assert.Empty(Combat.Corpses);
            Assert.True(player.Health > 1, $"health {player.Health} was not restored");
            Assert.Equal(0u, player.Combat.DeathTimer);
            Assert.Equal(0, player.GetByte(UpdateFields.PlayerFieldBytes, 0) & MapCombat.FieldByteReleaseTimer);
        }

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void Sanity_TheNormalReclaim_YieldsAFullyAlivePlayer()
    {
        using var rig = new Rig();

        Assert.True(rig.Combat.TryReclaimCorpse(rig.Ghost));

        rig.AssertFullyAlive(rig.Ghost);
        Assert.Equal(rig.Ghost.MaxHealth / 2, rig.Ghost.Health);
    }

    [Theory]
    [InlineData(Throwing.RemoveGhostForm)]
    [InlineData(Throwing.OnResurrected)]
    public void TryReclaimCorpse_WithAThrowingHook_StillEndsFullyAlive(Throwing which)
    {
        using var rig = new Rig();
        rig.Throw(which);

        _ = Record.Exception(() => rig.Combat.TryReclaimCorpse(rig.Ghost)); // the escape itself is asserted in the fault test; here the state is

        rig.AssertFullyAlive(rig.Ghost);
    }

    [Theory]
    [InlineData(Throwing.RemoveGhostForm)]
    [InlineData(Throwing.OnResurrected)]
    public void ResurrectAtSpiritHealer_WithAThrowingHook_StillEndsFullyAlive(Throwing which)
    {
        using var rig = new Rig();
        rig.Throw(which);

        _ = Record.Exception(() => rig.Combat.ResurrectAtSpiritHealer(rig.Ghost));

        rig.AssertFullyAlive(rig.Ghost);
    }

    [Theory]
    [InlineData(Throwing.RemoveGhostForm)]
    [InlineData(Throwing.OnResurrected)]
    public void AcceptingACreatureResurrection_WithAThrowingHook_StillEndsFullyAlive(Throwing which)
    {
        using var rig = new Rig();
        var healer = new CombatTestUnit();
        healer.SetUInt32(UpdateFields.UnitNpcFlags, (uint)NpcFlags.SpiritHealer);
        healer.Spawn(rig.Kit.World.GetMap(0), 10, 0);
        rig.CastAtCorpse(healer, Resurrection);
        rig.Throw(which);

        _ = Record.Exception(() => rig.Service.Respond(rig.Ghost, healer.Guid, accept: true));

        rig.AssertFullyAlive(rig.Ghost);
    }

    [Theory]
    [InlineData(Throwing.RemoveGhostForm)]
    [InlineData(Throwing.OnResurrected)]
    public void AHookFault_IsRecordedOnceInTheFaultLedger_AndDoesNotEscapeToTheCaller(Throwing which)
    {
        using var rig = new Rig();
        rig.Throw(which, () => new FaultTestException("atomic-resurrection hook fault " + which));
        long before = LedgerCount();

        bool reclaimed = false;
        Exception? escaped = Record.Exception(() => reclaimed = rig.Combat.TryReclaimCorpse(rig.Ghost));

        Assert.Null(escaped);
        Assert.True(reclaimed);
        Assert.Equal(before + 1, LedgerCount());
        Assert.All(FaultLedger.Shared.Snapshot().Where(s => s.ExceptionType == typeof(FaultTestException).FullName),
            s => Assert.Contains('.', s.Site)); // a dotted site name (CLAUDE.md error-handling idiom)
    }

    private static long LedgerCount()
        => FaultLedger.Shared.Snapshot().Where(s => s.ExceptionType == typeof(FaultTestException).FullName).Sum(s => s.Count);

    [Fact]
    public void VerifyPlayerLifeInvariants_HealsAnAliveGhost_AuraAndFlagWalk_AndLeavesTheCorpseAlone()
    {
        using var rig = new Rig();
        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Ghost, CombatConstants.WispGhostSpellId, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(rig.Kit.System.HasAura(rig.Ghost, CombatConstants.WispGhostSpellId));
        // The half-state a throwing hook used to leave: Alive, ghost flag and both auras on, the corpse still in the world.
        rig.Ghost.Combat.DeathState = DeathState.Alive;
        rig.Ghost.Health = 500;
        Assert.NotEqual(0u, (uint)(rig.Ghost.Flags & PlayerFlags.Ghost));
        Assert.Single(rig.Combat.Corpses);

        Assert.True(rig.Combat.VerifyPlayerLifeInvariants(rig.Ghost));

        Assert.Equal(0u, (uint)(rig.Ghost.Flags & PlayerFlags.Ghost));
        Assert.False(rig.Kit.System.HasAura(rig.Ghost, CombatConstants.GhostSpellId), "ghost aura 8326 left on an Alive player");
        Assert.False(rig.Kit.System.HasAura(rig.Ghost, CombatConstants.WispGhostSpellId), "wisp aura 20584 left on an Alive player");
        PendingMovementChange? walk = rig.Ghost.Locomotion.Pending.Changes.LastOrDefault(c => c.Type == MovementChangeType.WaterWalk);
        Assert.NotNull(walk);
        Assert.False(walk!.Apply, "land walk was not ordered");
        // a transport resurrection legitimately leaves the corpse (vmangos Transport.cpp:172-173), so the invariant check leaves it too
        Assert.NotNull(rig.Ghost.Combat.Corpse);
        Assert.Single(rig.Combat.Corpses);
    }

    [Fact]
    public void VerifyPlayerLifeInvariants_RemovesTheGhostAuraFromAnAlivePlayerWhoseGhostFlagIsAlreadyClear()
    {
        using var rig = new Rig();
        // Alive, PLAYER_FLAGS_GHOST already clear, but the 8326 aura (and so UNIT_VIS_FLAGS_GHOST) still on.
        rig.Ghost.Combat.DeathState = DeathState.Alive;
        rig.Ghost.Health = 500;
        rig.Ghost.Flags &= ~PlayerFlags.Ghost;
        Assert.True(rig.Kit.System.HasAura(rig.Ghost, CombatConstants.GhostSpellId));
        Assert.NotEqual(0, rig.Ghost.GetByte(UpdateFields.UnitFieldBytes1, GhostAuras.VisFlagByte) & GhostAuras.VisFlagGhost);

        Assert.True(rig.Combat.VerifyPlayerLifeInvariants(rig.Ghost));

        Assert.False(rig.Kit.System.HasAura(rig.Ghost, CombatConstants.GhostSpellId), "ghost aura 8326 left on an Alive player");
        Assert.Equal(0u, (uint)(rig.Ghost.Flags & PlayerFlags.Ghost));
    }

    [Fact]
    public void TryReclaimCorpse_InterruptsACasterCastingResurrectionAtTheGhostsUnitGuid()
    {
        using var rig = new Rig();
        rig.CastAtCorpse(rig.Caster, SlowResurrection, withUnit: true); // target: corpse + the ghost's unit guid (what Unit::InterruptSpellsCastedOnMe matches)
        Assert.Equal(SpellCastState.Preparing, rig.Kit.System.GetState(rig.Caster.Guid)!.CurrentCast!.State);

        Assert.True(rig.Combat.TryReclaimCorpse(rig.Ghost));

        SpellCast? cast = rig.Kit.System.GetState(rig.Caster.Guid)?.CurrentCast;
        Assert.True(cast is null || cast.State == SpellCastState.Finished, $"the resurrection cast is still {cast?.State}");
        rig.Kit.Advance(2500); // the cast bar would have run out
        Assert.Null(ResurrectionRequests.Get(rig.Ghost));
        Assert.DoesNotContain(rig.GhostSession.Sent, p => p.Opcode == WorldOpcode.SmsgResurrectRequest);
        rig.AssertFullyAlive(rig.Ghost);
    }

    [Fact]
    public void TryReclaimCorpse_DoesNotInterruptACasterWhoseResurrectionTargetsOnlyTheCorpse()
    {
        using var rig = new Rig();
        rig.CastAtCorpse(rig.Caster, SlowResurrection, withUnit: false); // vmangos matches the unit target guid only (Unit.cpp:10181-10215)
        Assert.Equal(SpellCastState.Preparing, rig.Kit.System.GetState(rig.Caster.Guid)!.CurrentCast!.State);

        Assert.True(rig.Combat.TryReclaimCorpse(rig.Ghost));

        Assert.Equal(SpellCastState.Preparing, rig.Kit.System.GetState(rig.Caster.Guid)!.CurrentCast!.State);
        rig.AssertFullyAlive(rig.Ghost);
    }
}

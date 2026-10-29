using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Death.Ghost;
using ArcaneCore.Game.Death.Resurrection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
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
/// Per-step fault isolation of MapCombat.ResurrectPlayer (vmangos Player::ResurrectPlayer, Player.cpp:4638-4667, runs to its end):
/// a fault injected at the start of any one <see cref="MapCombat.ResurrectStep"/> must not escape the entry point, is recorded once in the
/// <see cref="FaultLedger"/>, lets every other step still run, and never leaves the Alive + PLAYER_FLAGS_GHOST / ghost-aura half-state.
/// The one exception is <c>SetAlive</c>: when the player never became Alive it stays a complete ghost (dead, flag, aura, corpse).
/// Run over three entry points: the corpse reclaim, the spirit healer, and accepting a creature resurrection.
/// The ghost form is the real 8326 aura (as in <see cref="AtomicResurrectionTests"/>).
/// </summary>
public sealed class ResurrectStepFaultTests
{
    private const long T = 1_700_000_000;
    private const uint Resurrection = 10881;

    public enum Entry
    {
        Reclaim,
        SpiritHealer,
        AcceptCreatureResurrection,
    }

    /// <summary>Private to this file so ledger counts never collide with other test classes running in parallel.</summary>
    private sealed class ResurrectStepFaultException(string message) : Exception(message);

    private static SpellInfo Res(uint id) => SpellTestKit.Spell(id,
        SpellTestKit.Effect(SpellEffectName.ResurrectNew, 500, SpellImplicitTarget.None, misc: 750)) with
    {
        Targets = (uint)SpellCastTargetFlags.CorpseAlly,
        CastTime = new SpellCastTime(0, 0, 0),
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

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Kit = new SpellTestKit(Res(Resurrection), GhostSpell, WispSpell);
            DeathHooks.Register(Kit.World, new DeathHooks(new DeathOptions { GhostFormAura = true }, new FixedDeathClock(T)));
            DeathSeams.Of(Kit.World).TryRegisterGhostForm(new GhostForm(Kit.World, () => Kit.System));
            Teleports = new TeleportService(Kit.World, _ => { }, _ => { });
            Service = new ResurrectionService(Kit.World, () => Teleports);
            Kit.System.Resurrection = Service;
            Ghost = CombatTestKit.AddPlayer(Kit.World, 2, 50, 0, new FakeSession(2));
            _ = CombatTestKit.AddPlayer(Kit.World, 1, 0, 0, new FakeSession(1));
            Kit.World.RunTick(1);
            Kit.World.GetMap(0).Combat.SpellMitigation = Kit.System; // what SpellFeature does in production
            Ghost.Health = 0;
            Combat.KillPlayer(Ghost);
            Assert.True(Combat.RepopPlayer(Ghost)); // a ghost at its corpse, wearing the real ghost aura
            CombatTestKit.AckPendingMovement(Ghost);
            Kit.World.RunTick(1);
            Ghost.Combat.GhostTime = T - 100_000; // the reclaim delay is long over
            Assert.True(Kit.System.HasAura(Ghost, CombatConstants.GhostSpellId));
            Assert.NotEqual(0u, (uint)(Ghost.Flags & PlayerFlags.Ghost));
            Assert.NotNull(Ghost.Combat.Corpse);
        }

        public SpellTestKit Kit { get; }

        public TeleportService Teleports { get; }

        public ResurrectionService Service { get; }

        public Player Ghost { get; }

        public MapCombat Combat => Kit.World.GetMap(0).Combat;

        /// <summary>Runs the entry point; returns what it returned (true for the accept path, which returns nothing).</summary>
        public bool Run(Entry entry)
        {
            switch (entry)
            {
                case Entry.Reclaim:
                    return Combat.TryReclaimCorpse(Ghost);
                case Entry.SpiritHealer:
                    return Combat.ResurrectAtSpiritHealer(Ghost);
                default:
                    Service.Respond(Ghost, _healer!.Guid, accept: true);
                    return true;
            }
        }

        /// <summary>The accept path needs a pending request from a creature: a spirit healer casting the resurrection at the corpse.</summary>
        public void PrepareAccept()
        {
            _healer = new CombatTestUnit();
            _healer.SetUInt32(UpdateFields.UnitNpcFlags, (uint)NpcFlags.SpiritHealer);
            _healer.Spawn(Kit.World.GetMap(0), 10, 0);
            var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.CorpseAlly, Corpse = Ghost.Combat.Corpse!.Guid };
            Assert.Equal(SpellCastResult.CastOk, Kit.System.CastSpell(_healer, Resurrection, targets, triggered: true));
        }

        private CombatTestUnit? _healer;

        public void Dispose() => Kit.Dispose();
    }

    private static long LedgerCount()
        => FaultLedger.Shared.Snapshot().Where(s => s.ExceptionType == typeof(ResurrectStepFaultException).FullName).Sum(s => s.Count);

    /// <summary>
    /// Steps the entry point never reaches. ResurrectPlayer's RestoreVitals step runs only for restorePercent &gt; 0; the accept path
    /// (MapCombat.CompleteResurrection) calls ResurrectPlayer(player, 0f) and sets the offered vitals itself, so the injection never fires.
    /// </summary>
    private static bool Reached(Entry entry, MapCombat.ResurrectStep step)
        => !(entry == Entry.AcceptCreatureResurrection && step == MapCombat.ResurrectStep.RestoreVitals);

    // xunit data cannot carry the internal ResurrectStep enum through a public test signature, so the step travels as its int value.
    public static IEnumerable<object[]> Cases()
    {
        foreach (Entry entry in Enum.GetValues<Entry>())
        {
            foreach (MapCombat.ResurrectStep step in Enum.GetValues<MapCombat.ResurrectStep>())
            {
                yield return new object[] { entry, (int)step };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AFaultInOneStep_IsRecordedOnce_DoesNotEscape_AndNeverLeavesTheHalfState(Entry entry, int stepValue)
    {
        var step = (MapCombat.ResurrectStep)stepValue;
        using var rig = new Rig();
        Player ghost = rig.Ghost;
        if (entry == Entry.AcceptCreatureResurrection)
        {
            rig.PrepareAccept();
        }

        if (step == MapCombat.ResurrectStep.RemoveGhostForm)
        {
            // the wisp aura as well, so the fallback's wisp removal is exercised
            Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(ghost, CombatConstants.WispGhostSpellId, SpellCastTargets.ForSelf(), triggered: true));
            Assert.True(rig.Kit.System.HasAura(ghost, CombatConstants.WispGhostSpellId));
        }

        rig.Combat.ResurrectFaultInjection = (p, s) =>
        {
            if (s == step && p == ghost)
            {
                throw new ResurrectStepFaultException("injected fault in " + s);
            }
        };
        long before = LedgerCount();

        bool result = false;
        Exception? escaped = Record.Exception(() => result = rig.Run(entry));

        Assert.Null(escaped);
        Assert.Equal(Reached(entry, step) ? before + 1 : before, LedgerCount());
        Assert.All(FaultLedger.Shared.Snapshot().Where(s => s.ExceptionType == typeof(ResurrectStepFaultException).FullName),
            s => Assert.Contains('.', s.Site)); // a dotted site name (CLAUDE.md error-handling idiom)

        // never the half-state
        bool alive = ghost.Combat.DeathState == DeathState.Alive;
        Assert.False(alive && (ghost.Flags & PlayerFlags.Ghost) != 0, "Alive with PLAYER_FLAGS_GHOST: the half-state");
        Assert.False(alive && rig.Kit.System.HasAura(ghost, CombatConstants.GhostSpellId), "Alive with ghost aura 8326");
        Assert.False(alive && rig.Kit.System.HasAura(ghost, CombatConstants.WispGhostSpellId), "Alive with ghost aura 20584");

        if (step == MapCombat.ResurrectStep.SetAlive)
        {
            // the player never became Alive: a complete ghost, its corpse still in the world
            Assert.Equal(DeathState.Dead, ghost.Combat.DeathState);
            Assert.NotEqual(0u, (uint)(ghost.Flags & PlayerFlags.Ghost));
            Assert.True(rig.Kit.System.HasAura(ghost, CombatConstants.GhostSpellId), "ghost aura 8326 lost on a ghost");
            Assert.NotNull(ghost.Combat.Corpse);
            Assert.Contains(ghost.Combat.Corpse, rig.Combat.Corpses);
            if (entry == Entry.Reclaim)
            {
                Assert.False(result, "TryReclaimCorpse reported success though the player is still dead");
            }

            return;
        }

        Assert.Equal(DeathState.Alive, ghost.Combat.DeathState);
        Assert.Equal(0u, (uint)(ghost.Flags & PlayerFlags.Ghost));
        Assert.False(rig.Kit.System.HasAura(ghost, CombatConstants.GhostSpellId), "ghost aura 8326 still on");
        Assert.Equal(0u, ghost.Combat.DeathTimer);

        // every property not owned by the faulted step is still committed
        int releaseTimer = ghost.GetByte(UpdateFields.PlayerFieldBytes, 0) & MapCombat.FieldByteReleaseTimer;
        if (step != MapCombat.ResurrectStep.ClearReleaseTimer)
        {
            Assert.Equal(0, releaseTimer);
        }

        if (step != MapCombat.ResurrectStep.ClearSelfResSpell)
        {
            Assert.Equal(0u, ghost.GetUInt32(UpdateFields.PlayerSelfResSpell));
        }

        if (step != MapCombat.ResurrectStep.RemoveCorpse)
        {
            Assert.Null(ghost.Combat.Corpse);
            Assert.Empty(rig.Combat.Corpses);
        }

        if (step == MapCombat.ResurrectStep.ClearGhostFlag)
        {
            PendingMovementChange? walk = ghost.Locomotion.Pending.Changes.LastOrDefault(c => c.Type == MovementChangeType.WaterWalk);
            Assert.NotNull(walk);
            Assert.False(walk!.Apply, "land walk was not ordered");
        }

        // Health: when the RestoreVitals step faults (Reclaim, SpiritHealer) the fallback still sets the restore health, so the
        // result is the same 50% (CombatConstants.CorpseReclaimRestorePercent) as without a fault. The accept path never reaches that
        // step (it takes the offered vitals).
        Assert.True(ghost.Health > 1, $"health {ghost.Health} was not restored");
        if (entry != Entry.AcceptCreatureResurrection)
        {
            Assert.Equal(ghost.MaxHealth / 2, ghost.Health);
        }

        if (step == MapCombat.ResurrectStep.RemoveGhostForm)
        {
            // the fallback strips both ghost auras directly, wisp included
            Assert.False(rig.Kit.System.HasAura(ghost, CombatConstants.GhostSpellId), "ghost aura 8326 survived the RemoveGhostForm fault");
            Assert.False(rig.Kit.System.HasAura(ghost, CombatConstants.WispGhostSpellId), "wisp aura 20584 survived the RemoveGhostForm fault");
        }

        if (step == MapCombat.ResurrectStep.RemoveCorpse)
        {
            // the fault kept the corpse referenced; the invariant check does not remove it (an Alive player may legitimately keep one)
            rig.Combat.ResurrectFaultInjection = null;
            _ = rig.Combat.VerifyPlayerLifeInvariants(ghost);
            Assert.Equal(DeathState.Alive, ghost.Combat.DeathState);
            Assert.Equal(0u, (uint)(ghost.Flags & PlayerFlags.Ghost));
            Assert.False(rig.Kit.System.HasAura(ghost, CombatConstants.GhostSpellId));
            Assert.NotNull(ghost.Combat.Corpse);
            Assert.Contains(ghost.Combat.Corpse, rig.Combat.Corpses);

            // a later removal without an injection takes it away
            rig.Combat.SpawnCorpseBones(ghost);
            Assert.Null(ghost.Combat.Corpse);
            Assert.Empty(rig.Combat.Corpses);
        }
    }

    [Fact]
    public void TheExternalShape_ResurrectPlayerThenSpawnCorpseBones_WithSetAliveFaulting_KeepsTheGhostAndItsCorpse()
    {
        using var rig = new Rig();
        Player ghost = rig.Ghost;
        rig.Combat.ResurrectFaultInjection = (p, s) =>
        {
            if (s == MapCombat.ResurrectStep.SetAlive && p == ghost)
            {
                throw new ResurrectStepFaultException("injected fault in " + s);
            }
        };

        rig.Combat.ResurrectPlayer(ghost, 1f, applySickness: false);
        rig.Combat.SpawnCorpseBones(ghost); // what DeathGmCommands, MatchRuntime, BattlegroundWorldHost and ScenarioDungeonBots do next

        Assert.Equal(DeathState.Dead, ghost.Combat.DeathState);
        Assert.NotEqual(0u, (uint)(ghost.Flags & PlayerFlags.Ghost));
        Assert.True(rig.Kit.System.HasAura(ghost, CombatConstants.GhostSpellId), "ghost aura 8326 lost on a ghost");
        Assert.NotNull(ghost.Combat.Corpse);
        Assert.Contains(ghost.Combat.Corpse, rig.Combat.Corpses);
    }

    [Fact]
    public void TheExternalShape_ResurrectPlayerThenSpawnCorpseBones_WithoutAnInjection_IsFullyAliveAndTheCorpseIsGone()
    {
        using var rig = new Rig();
        Player ghost = rig.Ghost;

        rig.Combat.ResurrectPlayer(ghost, 1f, applySickness: false);
        rig.Combat.SpawnCorpseBones(ghost);

        Assert.Equal(DeathState.Alive, ghost.Combat.DeathState);
        Assert.Equal(0u, (uint)(ghost.Flags & PlayerFlags.Ghost));
        Assert.False(rig.Kit.System.HasAura(ghost, CombatConstants.GhostSpellId), "ghost aura 8326 still on");
        Assert.Null(ghost.Combat.Corpse);
        Assert.Empty(rig.Combat.Corpses);
    }

    [Fact]
    public void WithoutAnInjection_TheSeamIsNullByDefault_AndTheNormalReclaimIsFullyAlive()
    {
        using var rig = new Rig();
        Assert.Null(rig.Combat.ResurrectFaultInjection); // production never sets it
        long before = LedgerCount();

        Assert.True(rig.Combat.TryReclaimCorpse(rig.Ghost));

        Assert.Equal(before, LedgerCount());
        Assert.Equal(DeathState.Alive, rig.Ghost.Combat.DeathState);
        Assert.Equal(0u, (uint)(rig.Ghost.Flags & PlayerFlags.Ghost));
        Assert.False(rig.Kit.System.HasAura(rig.Ghost, CombatConstants.GhostSpellId));
        Assert.Null(rig.Ghost.Combat.Corpse);
        Assert.Empty(rig.Combat.Corpses);
        Assert.Equal(rig.Ghost.MaxHealth / 2, rig.Ghost.Health);
    }
}

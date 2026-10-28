using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Death.Visibility;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// Living and dead do not see each other (vmangos Player::IsVisibleInGridForPlayer, Player.cpp:18710-18743, Creature::
/// IsVisibleInGridForPlayer, Creature.cpp:2477-2509, Unit::IsInvisibleForAlive, Unit.cpp:7703-7710), with the real map visibility
/// machinery: create and out-of-range blocks follow the death state.
/// </summary>
public sealed class GhostVisibilityTests
{
    private const long T = 1_700_000_000;

    private sealed class Rig : IDisposable
    {
        public Rig(ISpellGroupResolver? groups = null)
        {
            World = TestWorld.CreateRuntime();
            DeathHooks.Register(World, new DeathHooks(new DeathOptions(), new FixedDeathClock(T)));
            Map = World.GetMap(0);
            Map.AddVisibilityRule(new GhostVisibilityRule(() => groups));
        }

        public WorldRuntime World { get; }

        public Map Map { get; }

        public Player Add(uint guid, float x, float y, Race race = Race.Human, AccountSecurity security = AccountSecurity.Player)
        {
            Player player = CombatTestKit.AddPlayer(World, guid, x, y, new FakeSession((int)guid, security), race);
            if (security != AccountSecurity.Player)
            {
                player.SetGameMaster(true);
            }

            return player;
        }

        public CombatTestUnit AddCreature(float x, float y, NpcFlags flags = NpcFlags.None)
        {
            var creature = new CombatTestUnit();
            creature.Relocate(x, y, 83.5f, 0f, 0);
            creature.SetUInt32(UpdateFields.UnitNpcFlags, (uint)flags);
            Map.AddObject(creature);
            return creature;
        }

        public void Tick() => World.RunTick(50);

        public bool Sees(Player viewer, WorldObject target) => viewer.VisibleObjects.Contains(target.Guid);

        /// <summary>Kill and release a player, then let it stand where <paramref name="x"/> says (its corpse stays where it died).</summary>
        public void Release(Player player, float? x = null)
        {
            player.Health = 0;
            Map.Combat.KillPlayer(player);
            Assert.True(Map.Combat.RepopPlayer(player));
            if (x is { } at)
            {
                player.Relocate(at, player.Y, player.Z, 0f, 0);
                player.NeedsVisibilityUpdate = true;
            }

            Tick();
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void ALivingPlayer_CannotSeeASpiritHealer_ButAGhostCan()
    {
        using var rig = new Rig();
        Player living = rig.Add(1, 0, 0);
        Player dying = rig.Add(2, 5, 0);
        CombatTestUnit healer = rig.AddCreature(10, 0, NpcFlags.SpiritHealer);
        CombatTestUnit guide = rig.AddCreature(11, 0, NpcFlags.SpiritGuide);
        rig.Tick();
        Assert.False(rig.Sees(living, healer));
        Assert.False(rig.Sees(living, guide));

        rig.Release(dying);

        Assert.True(rig.Sees(dying, healer));
        Assert.True(rig.Sees(dying, guide));
        Assert.False(rig.Sees(living, healer)); // still not
    }

    [Fact]
    public void AGhost_OnlySeesTheLivingCreaturesNearItsCorpse_AndTheLivingSeeAllOfThem()
    {
        using var rig = new Rig();
        Player dying = rig.Add(1, 0, 0);
        Player living = rig.Add(2, 3, 0);
        CombatTestUnit near = rig.AddCreature(30, 0);
        CombatTestUnit far = rig.AddCreature(60, 0);
        rig.Tick();
        Assert.True(rig.Sees(dying, near));
        Assert.True(rig.Sees(dying, far));

        rig.Release(dying, x: 20); // the corpse stays at 0: 30 yd away from `near` (inside 45), 60 yd from `far` (outside)

        Assert.True(rig.Sees(dying, near));
        Assert.False(rig.Sees(dying, far));
        Assert.True(rig.Sees(living, near));
        Assert.True(rig.Sees(living, far));
    }

    [Fact]
    public void AGhost_SeesTheLivingPlayersWithin100YardsOfItsCorpse_AndNotAnyOther()
    {
        using var rig = new Rig();
        Player dying = rig.Add(1, 0, 0);
        Player near = rig.Add(2, 90, 0);
        Player far = rig.Add(3, 150, 0, race: Race.Human);
        rig.Tick();

        rig.Release(dying, x: 120);

        Assert.True(rig.Sees(dying, near));
        Assert.False(rig.Sees(dying, far));
    }

    [Fact]
    public void TheLiving_DoNotSeeAGhost_AndAGhostIsDestroyedForThem()
    {
        using var rig = new Rig();
        Player dying = rig.Add(1, 0, 0);
        Player living = rig.Add(2, 5, 0);
        rig.Tick();
        Assert.True(rig.Sees(living, dying));

        rig.Release(dying);

        Assert.False(rig.Sees(living, dying));
    }

    [Fact]
    public void ResurrectingMakesTheTwoSeeEachOtherAgain_WithCreateBlocks()
    {
        using var rig = new Rig();
        Player dying = rig.Add(1, 0, 0);
        Player living = rig.Add(2, 5, 0);
        rig.Tick();
        rig.Release(dying);
        Assert.False(rig.Sees(living, dying));

        rig.Map.Combat.ResurrectPlayer(dying, 0.5f, applySickness: false);
        rig.Tick();

        Assert.True(rig.Sees(living, dying));
        Assert.True(rig.Sees(dying, living));
    }

    [Fact]
    public void APlayerWaitingAtItsBody_StillSeesTheLivingWorld_AndIsSeenByIt()
    {
        using var rig = new Rig();
        Player dying = rig.Add(1, 0, 0);
        Player living = rig.Add(2, 5, 0);
        CombatTestUnit creature = rig.AddCreature(80, 0);
        CombatTestUnit healer = rig.AddCreature(10, 0, NpcFlags.SpiritHealer);
        rig.Tick();

        dying.Health = 0;
        rig.Map.Combat.KillPlayer(dying); // CORPSE state, release timer running
        dying.NeedsVisibilityUpdate = true;
        rig.Tick();

        Assert.True(rig.Sees(living, dying));
        Assert.True(rig.Sees(dying, living));
        Assert.True(rig.Sees(dying, creature));
        Assert.True(rig.Sees(dying, healer)); // not alive: IsInvisibleForAlive does not separate them (Unit.cpp:6366)
    }

    [Fact]
    public void TwoGhostsOfTheSameTeam_SeeEachOther_ButNotAGhostOfTheOtherTeam()
    {
        using var rig = new Rig();
        Player a = rig.Add(1, 0, 0);
        Player b = rig.Add(2, 50, 0);
        Player enemy = rig.Add(3, 60, 0, race: Race.Orc);
        rig.Tick();

        rig.Release(a);
        rig.Release(b);
        rig.Release(enemy);

        Assert.True(rig.Sees(a, b));
        Assert.True(rig.Sees(b, a));
        Assert.False(rig.Sees(a, enemy));
    }

    [Fact]
    public void ARaidMember_SeesAGhostAndIsSeenByIt_BeyondTheHundredYardsOfTheCorpse()
    {
        Player[] pair = [];
        using var rig = new Rig(groups: new LateRaid(() => pair));
        Player ghost = rig.Add(1, 0, 0);
        Player mate = rig.Add(2, 140, 0);
        Player stranger = rig.Add(3, 141, 0);
        pair = [ghost, mate];
        rig.Tick();

        // The corpse stays at x = 0; the ghost stands at x = 60: both are in view range of each other, 140 yd from the corpse.
        rig.Release(ghost, x: 60);

        Assert.True(rig.Sees(mate, ghost));      // not a ghost-friend, not near the corpse: the raid rule alone
        Assert.True(rig.Sees(ghost, mate));
        Assert.False(rig.Sees(stranger, ghost)); // a living stranger never sees a ghost
        Assert.False(rig.Sees(ghost, stranger)); // and a ghost sees a living stranger only near its corpse
    }

    private sealed class LateRaid(Func<Player[]> members) : ISpellGroupResolver
    {
        public IReadOnlyCollection<ObjectGuid> GetGroupMembers(Unit unit, bool raid)
        {
            Player[] group = members();
            return group.Any(m => ReferenceEquals(m, unit)) ? [.. group.Select(m => m.Guid)] : [unit.Guid];
        }
    }

    [Fact]
    public void AGameMaster_SeesGhostsAndSpiritHealers()
    {
        using var rig = new Rig();
        Player dying = rig.Add(1, 0, 0);
        Player gm = rig.Add(2, 5, 0, security: AccountSecurity.GameMaster);
        CombatTestUnit healer = rig.AddCreature(10, 0, NpcFlags.SpiritHealer);
        rig.Tick();

        rig.Release(dying);

        Assert.True(rig.Sees(gm, dying));
        Assert.True(rig.Sees(gm, healer));
    }

    [Fact]
    public void TheRuleHasNoOpinionAboutObjectsThatAreNotUnits()
    {
        using var rig = new Rig();
        Player viewer = rig.Add(1, 0, 0);
        var corpse = Corpse.CreateFor(viewer, false);

        Assert.True(new GhostVisibilityRule().CanSee(viewer, corpse, false, false));
    }

    [Fact]
    public void TheAggroRate_ScalesTheCreatureRange()
    {
        using var rig = new Rig();
        Player ghost = rig.Add(1, 0, 0);
        CombatTestUnit creature = rig.AddCreature(30, 0);
        rig.Tick();
        rig.Release(ghost, x: 400);
        var rule = new GhostVisibilityRule(null, () => 0.5f);

        Assert.False(rule.CanSee(ghost, creature, false, false));         // 30 yd > 45 x 0.5
        Assert.True(new GhostVisibilityRule().CanSee(ghost, creature, false, false)); // 30 yd < 45
    }

    [Fact]
    public void SameTickResurrectAndDeath_KeepsNearbyCreaturesVisible_AndFlushesAliveUpdateBeforeDeath()
    {
        using var rig = new Rig();
        var session = new FakeSession(1);
        Player dying = CombatTestKit.AddPlayer(rig.World, 1, 0, 0, session);
        CombatTestUnit anubisath = rig.AddCreature(10, 0);
        rig.Tick();
        Assert.True(rig.Sees(dying, anubisath));

        rig.Release(dying);
        Assert.Equal(DeathState.Dead, dying.Combat.DeathState);
        Assert.NotEqual(0, dying.GetByte(UpdateFields.PlayerFieldBytes, 0) & MapCombat.FieldByteReleaseTimer);

        session.Sent.Clear();

        // Simulate reclaiming the corpse in Phase 1a and being killed by a creature in Phase 1c of the same tick:
        rig.Map.Combat.ResurrectPlayer(dying, 0.5f, applySickness: false);
        rig.Map.Combat.SpawnCorpseBones(dying);
        Assert.Equal(0, dying.GetByte(UpdateFields.PlayerFieldBytes, 0) & MapCombat.FieldByteReleaseTimer);

        rig.Map.Combat.Kill(anubisath, dying);
        Assert.Equal(DeathState.JustDied, dying.Combat.DeathState);
        Assert.Equal(0u, dying.Combat.DeathTimer);
        Assert.Null(dying.Combat.Corpse);
        Assert.True(new GhostVisibilityRule().CanSee(dying, anubisath, alreadyVisible: true, detect: false));

        // Flush the tick (visibility + values):
        rig.Map.Update(50);

        // 1) The living creature must remain visible (not destroyed as if the JustDied player were a corpse-less ghost).
        Assert.True(rig.Sees(dying, anubisath));

        // 2) Two separate SMSG_UPDATE_OBJECT packets must have been sent: first the resurrected alive state, then the death state.
        int updatePackets = session.Sent.Count(p => p.Opcode == WorldOpcode.SmsgUpdateObject);
        Assert.True(updatePackets >= 2, $"Expected at least 2 SMSG_UPDATE_OBJECT packets (alive then dead), got {updatePackets}");
    }

    [Fact]
    public void CancellingLogoutWhileDeadAtBody_KeepsTheCorpseRooted()
    {
        using var rig = new Rig();
        Player dying = rig.Add(1, 0, 0);
        CombatTestUnit creature = rig.AddCreature(5, 0);
        rig.Tick();

        dying.BeginLogout(1000);
        Assert.True(dying.IsRooted);

        rig.Map.Combat.Kill(creature, dying);
        Assert.Equal(DeathState.JustDied, dying.Combat.DeathState);
        Assert.True(dying.IsRooted);

        dying.CancelLogout();
        Assert.True(dying.IsRooted);

        rig.Map.Combat.KillPlayer(dying);
        Assert.Equal(DeathState.Corpse, dying.Combat.DeathState);
        dying.CancelLogout();
        Assert.True(dying.IsRooted);
    }

    [Fact]
    public void DyingPlayerDisconnectingDuringFarTeleportTransit_StillRunsRepopPlayerOnLogout()
    {
        using var rig = new Rig();
        Player dying = rig.Add(1, 0, 0);
        CombatTestUnit creature = rig.AddCreature(5, 0);
        rig.Tick();

        rig.Map.Combat.Kill(creature, dying);
        rig.Map.Combat.KillPlayer(dying);
        Assert.Equal(DeathState.Corpse, dying.Combat.DeathState);
        Assert.True(dying.Combat.DeathTimer > 0);

        // Simulate ExecuteTeleportFar removing the player from the map and placing it into _transit before disconnect:
        rig.Map.RemovePlayer(dying);
        rig.Map.BeginTransit(dying);
        Assert.Null(dying.Map);
        Assert.True(rig.Map.IsInTransit(dying));

        rig.World.RemovePlayer(dying);

        Assert.Equal(DeathState.Dead, dying.Combat.DeathState);
        Assert.NotEqual(PlayerFlags.None, dying.Flags & PlayerFlags.Ghost);
        Assert.Equal(1u, dying.Health);
    }

    [Fact]
    public void LifeStateInvariants_HealStaleGhostFlagInAliveState_AndZeroHealthInDeadState()
    {
        using var rig = new Rig();
        Player player = rig.Add(1, 0, 0);
        rig.Tick();

        // Stale ghost flag while Alive:
        player.Flags |= PlayerFlags.Ghost;
        rig.Tick();
        Assert.Equal(PlayerFlags.None, player.Flags & PlayerFlags.Ghost);

        // Stale release timer byte and DeathTimer while Alive:
        player.SetByte(UpdateFields.PlayerFieldBytes, 0,
            (byte)(player.GetByte(UpdateFields.PlayerFieldBytes, 0) | MapCombat.FieldByteReleaseTimer));
        player.Combat.DeathTimer = 60_000;
        rig.Tick();
        Assert.Equal(0, player.GetByte(UpdateFields.PlayerFieldBytes, 0) & MapCombat.FieldByteReleaseTimer);
        Assert.Equal(0u, player.Combat.DeathTimer);

        // Non-finite coordinates healed on tick and sanitized in CreateSnapshot:
        player.Relocate(float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.NaN, 0);
        var snap = player.CreateSnapshot(0);
        Assert.True(float.IsFinite(snap.X) && float.IsFinite(snap.Y) && float.IsFinite(snap.Z) && float.IsFinite(snap.Orientation));
        rig.Tick();
        Assert.True(float.IsFinite(player.X) && float.IsFinite(player.Y) && float.IsFinite(player.Z) && float.IsFinite(player.Orientation));

        // Zero health and missing Ghost flag while Dead (ghost):
        rig.Release(player);
        player.Health = 0;
        player.Flags &= ~PlayerFlags.Ghost;
        rig.Tick();
        Assert.Equal(1u, player.Health);
        Assert.NotEqual(PlayerFlags.None, player.Flags & PlayerFlags.Ghost);
    }

    [Fact]
    public void CombatAndVisibilityInvariants_PruneStaleAttackersThreatHoldersAndOrphanVisibleGuids()
    {
        using var rig = new Rig();
        Player player = rig.Add(1, 0, 0);
        CombatTestUnit creature = rig.AddCreature(5, 0);
        rig.Tick();

        // Inject an orphan GUID into VisibleObjects that does not exist on the map:
        var orphanGuid = ObjectGuid.WithEntry(HighGuid.Unit, 9999, 999999);
        player.VisibleObjects.Add(orphanGuid);
        rig.Map.NoteRemoval(); // the visible-set pass is epoch-gated: an orphan is healed after a removal or on the 64th update
        player.NeedsVisibilityUpdate = true;

        // Inject a dead creature into AttackersInternal and ThreatenedByInternal without ThreatList entry:
        creature.Health = 0;
        creature.Combat.DeathState = DeathState.Dead;
        player.Combat.AttackersInternal.Add(creature);
        player.Combat.ThreatenedByInternal.Add(creature);
        player.Combat.CombatTimer = 0;
        player.UnitFlags |= UnitFlags.InCombat;

        rig.Tick();

        Assert.DoesNotContain(orphanGuid, player.VisibleObjects);
        Assert.Empty(player.Combat.Attackers);
        Assert.Empty(player.Combat.ThreatenedByInternal);
        Assert.False(player.Combat.IsInCombat);
    }

    [Fact]
    public void Gaeriyan9299AndGhostAuraCreatures_AreInvisibleToLiving_AndVisibleToGhostsAtAnyDistanceFromCorpse()
    {
        using var rig = new Rig();
        Player living = rig.Add(1, 0, 0);
        Player dying = rig.Add(2, 5, 0);

        CombatTestUnit gaeriyan = rig.AddCreature(120, 0, NpcFlags.Gossip | NpcFlags.QuestGiver);
        gaeriyan.SetUInt32(UpdateFields.ObjectFieldEntry, GhostVisibilityRule.GaeriyanEntry);

        CombatTestUnit ghostAuraNpc = rig.AddCreature(125, 0, NpcFlags.QuestGiver);
        ghostAuraNpc.SetUInt32(UpdateFields.UnitFieldAura, GhostVisibilityRule.SpellGhostAura);

        CombatTestUnit visFlagNpc = rig.AddCreature(130, 0, NpcFlags.QuestGiver);
        visFlagNpc.SetByte(UpdateFields.UnitFieldBytes1, GhostAuras.VisFlagByte, GhostAuras.VisFlagGhost);

        living.Relocate(115, 0, living.Z, 0f, 0);
        living.NeedsVisibilityUpdate = true;
        rig.Tick();

        Assert.False(rig.Sees(living, gaeriyan));
        Assert.False(rig.Sees(living, ghostAuraNpc));
        Assert.False(rig.Sees(living, visFlagNpc));

        // dying dies at x=5, releases and walks to x=115 (110 yd from corpse, beyond the 45 yd living-creature range):
        rig.Release(dying, x: 115);

        Assert.True(rig.Sees(dying, gaeriyan));
        Assert.True(rig.Sees(dying, ghostAuraNpc));
        Assert.True(rig.Sees(dying, visFlagNpc));
        Assert.False(rig.Sees(living, gaeriyan));
    }
}


using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Death.Visibility;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
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
}

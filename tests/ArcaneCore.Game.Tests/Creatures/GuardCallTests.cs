using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Tests.Collision;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Static flag CALLS_GUARDS (0x08000000): a creature that sees a hostile player within its detection range, or enters combat, calls the
/// guards (vmangos BasicAI::MoveInLineOfSight / SummonGuard, AI/BasicAI.cpp:49-105; Creature::OnEnterCombat, Objects/Creature.cpp:3689-3690;
/// GuardMgr::SummonGuard, GuardMgr.cpp:421-457). In a town with a guard post it shouts and the post sends a guard of the enemy's opposite
/// team that attacks the enemy (one charge, 10 s cooldown); elsewhere the nearest idle friendly guard within 50 yd attacks. classic-db
/// sets the flag on no template (vmangos data does), so the tests use synthetic templates; the area comes from a test locator.
/// </summary>
public sealed class GuardCallTests
{
    private const uint CallsGuards = 0x08000000;
    private const uint CivilianEntry = 7101;
    private const uint StormwindGuardEntry = 68;   // Goldshire's post (vmangos GuardMgr.cpp: AREA_GOLDSHIRE -> NPC_STORMWIND_CITY_GUARD)
    private const uint Goldshire = 87;
    private const uint NoPostArea = 40;            // Westfall: no post
    private const uint HumanFaction = 12;          // Stormwind faction template: the human call text by faction

    private static CreatureTemplate Civilian(uint flags = CallsGuards)
        => Template(CivilianEntry) with { StaticFlags1 = flags, Civilian = true, Faction = HumanFaction, Detection = 18 };

    private static CreatureTemplate GuardTemplate(uint entry = StormwindGuardEntry)
        => Template(entry) with { ExtraFlags = 0x400, Faction = HumanFaction };

    private static BroadcastTextCatalog Texts() => new([
        new BroadcastText(GuardPostTable.TextGuardHuman, "Guards! Help me!", "", 0, 7, 0, [], []),
    ]);

    /// <summary>Creatures are friends of each other and enemies of every player.</summary>
    private sealed class TownHostility : ICreatureHostility
    {
        public bool IsHostile(Creature creature, Unit target) => target is Player;

        public bool CanAssist(Creature helper, Creature caller) => true;

        public bool IsFriendly(Creature creature, Unit other) => other is Creature;
    }

    private sealed record Town(WorldRuntime World, Map Map, CreatureMapSystem System, Creature Civilian, GuardPostTable Posts) : IDisposable
    {
        public void Dispose() => World.Dispose();

        public IEnumerable<Creature> Guards => System.Creatures.Where(c => c.Template.Entry == StormwindGuardEntry);
    }

    private static Town Start(uint areaId = Goldshire, CreatureTemplate? civilian = null, GuardPostTable? posts = null, IEnumerable<CreatureSpawn>? more = null,
        IEnumerable<CreatureTemplate>? moreTemplates = null, Func<Creature, Team?>? teamOf = null)
    {
        posts ??= new GuardPostTable();
        CreatureContent content = new([civilian ?? Civilian(), GuardTemplate(), .. moreTemplates ?? []], [Spawn(1, CivilianEntry, 0, 0), .. more ?? []],
            [], [], [], new CreatureAiContent([], [], Texts()));
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices
        {
            Hostility = new TownHostility(),
            GuardPosts = posts,
            AreaOf = _ => areaId,
            TeamOf = teamOf,
        }, new CreatureOptions { AiRelocationNotifyDelayMs = 3_600_000, RespawnPacifyMs = 0 });
        AddPlayer(world, 99, 0, -80); // loads the town's grids; far outside every range
        Creature spawned = system.Creatures.Single(c => c.Template.Entry == CivilianEntry);
        return new Town(world, map, system, spawned, posts);
    }

    private static (Player Player, FakeSession Session) Horde(WorldRuntime world, uint guid, float x, float y)
    {
        var session = new FakeSession((int)guid);
        Player player = TestWorld.CreatePlayer(guid, x, y, session, race: Race.Orc);
        world.AddPlayer(player);
        world.RunTick(0);
        session.Clear();
        return (player, session);
    }

    [Fact]
    public void AHostilePlayerInSight_MakesTheCivilianShout_AndThePostSendsAGuardThatAttacksIt()
    {
        using Town t = Start();
        (Player orc, FakeSession session) = Horde(t.World, 1, 10, 0); // within the 18 yd detection range

        t.Civilian.AI!.MoveInLineOfSight(orc);

        Creature guard = Assert.Single(t.Guards);
        // GetNearPoint(civilian, x, y, z, 0, 5, 0): the civilian's bounding radius + 5 yd at the absolute angle 0 (Object.cpp:2726-2729).
        Assert.Equal(t.Civilian.X + t.Civilian.BoundingRadius + GuardPostTable.SummonDistance, guard.X, 2);
        Assert.Equal(t.Civilian.Y, guard.Y, 2);
        Assert.Same(orc, guard.Combat.Victim);
        Assert.Null(t.Civilian.Combat.Victim); // a civilian does not attack
        MonsterChat said = ParseMonsterChat(Assert.Single(Packets(session, WorldOpcode.SmsgMessagechat)));
        Assert.Equal(ChatType.MonsterSay, said.Type);
        Assert.Equal("Guards! Help me!", said.Message);
        Assert.Equal(GuardPostTable.MaxCharges - 1, t.Posts.ChargesOf(Goldshire));
    }

    [Fact]
    public void AfterASuccessfulCall_TheCivilianStopsCalling_UntilItsGuardIsGone()
    {
        using Town t = Start();
        (Player orc, _) = Horde(t.World, 1, 10, 0);
        t.Civilian.AI!.MoveInLineOfSight(orc);
        Assert.Single(t.Guards);

        Run(t.World, 11_000); // past the post's cooldown
        t.Civilian.AI!.MoveInLineOfSight(orc);
        Assert.Single(t.Guards); // vmangos BasicAI: m_bCanSummonGuards = !SummonGuard(...)

        t.System.Despawn(t.Guards.Single());
        Run(t.World, 100);
        t.Civilian.AI!.MoveInLineOfSight(orc);
        Assert.Single(t.Guards); // SummonedCreatureDespawn gave the call back
    }

    [Fact]
    public void ThePost_HasTenSecondsOfCooldown_AndTenCharges_RechargingOnePerMinute()
    {
        var posts = new GuardPostTable();
        Assert.Equal(GuardPostUse.Used, posts.TryUse(Goldshire, Team.Alliance, 1_000).Use);
        Assert.Equal(GuardPostUse.Unavailable, posts.TryUse(Goldshire, Team.Alliance, 10_999).Use);
        Assert.Equal(new GuardPostCall(GuardPostUse.Used, StormwindGuardEntry), posts.TryUse(Goldshire, Team.Alliance, 11_000));
        Assert.Equal(new GuardPostCall(GuardPostUse.Used, 0), posts.TryUse(Goldshire, Team.Horde, 21_000)); // Goldshire has no horde guard

        for (long now = 31_000; now < 59_999; now += 10_000)
        {
            posts.TryUse(Goldshire, Team.Alliance, now); // 31, 41, 51 s: charges 6, 5, 4
        }

        Assert.Equal(4u, posts.ChargesOf(Goldshire));
        Assert.Equal(GuardPostUse.Used, posts.TryUse(Goldshire, Team.Alliance, 61_000).Use); // +1 at the minute, then -1
        Assert.Equal(4u, posts.ChargesOf(Goldshire));
        Assert.Equal(GuardPostUse.Used, posts.TryUse(Goldshire, Team.Alliance, 245_000).Use); // three more minutes: +3, then -1
        Assert.Equal(6u, posts.ChargesOf(Goldshire));
        Assert.Equal(GuardPostUse.NoPost, posts.TryUse(NoPostArea, Team.Alliance, 61_000).Use);

        // The uint server clock wraps after 49.7 days: the post starts afresh instead of waiting for its old cooldown.
        Assert.Equal(GuardPostUse.Used, posts.TryUse(Goldshire, Team.Alliance, uint.MaxValue - 5_000).Use);
        Assert.Equal(GuardPostUse.Used, posts.TryUse(Goldshire, Team.Alliance, 3_000).Use);
        Assert.Equal(GuardPostTable.MaxCharges - 1, posts.ChargesOf(Goldshire));
    }

    [Fact]
    public void WhenThePostIsOnCooldown_TheCivilianKeepsTryingOnSight()
    {
        var posts = new GuardPostTable();
        using Town t = Start(posts: posts);
        Assert.Equal(GuardPostUse.Used, posts.TryUse(Goldshire, Team.Alliance, 0).Use); // another civilian called at server time 0
        (Player orc, _) = Horde(t.World, 1, 10, 0);

        t.Civilian.AI!.MoveInLineOfSight(orc);
        Assert.Empty(t.Guards);

        Run(t.World, 10_100);
        t.Civilian.AI!.MoveInLineOfSight(orc);
        Assert.Single(t.Guards);
    }

    /// <summary>
    /// vmangos GetNearPoint with DetectPosCollision (default on, World.cpp:751; Object.cpp:2776-2825): a first point the civilian cannot
    /// see is given up for another angle around it at the same distance that it can see.
    /// </summary>
    [Fact]
    public void AWallEastOfTheCivilian_PutsTheGuardWhereTheCivilianCanSeeIt()
    {
        using Town t = Start();
        WorldCollision.Of(t.World).Install(new CollisionSeamTests.WallAtX(3f));
        (Player orc, _) = Horde(t.World, 1, -10, 0);

        t.Civilian.AI!.MoveInLineOfSight(orc);

        Creature guard = Assert.Single(t.Guards);
        Assert.True(guard.X < 3f, $"the guard appeared behind the wall at x = {guard.X}");
        Assert.True(t.Map.Collision.IsWithinLineOfSight(t.Civilian, guard));
        Assert.Equal(t.Civilian.BoundingRadius + GuardPostTable.SummonDistance, Distance2D(guard, t.Civilian), 2);
    }

    [Fact]
    public void BeyondTheDetectionRange_OrAgainstAFriend_NobodyIsCalled()
    {
        using Town far = Start();
        (Player distant, _) = Horde(far.World, 1, 25, 0);
        far.Civilian.AI!.MoveInLineOfSight(distant);
        Assert.Empty(far.Guards);

        using Town plain = Start(civilian: Civilian(flags: 0));
        (Player orc, _) = Horde(plain.World, 2, 10, 0);
        plain.Civilian.AI!.MoveInLineOfSight(orc);
        Assert.Empty(plain.Guards);
    }

    [Fact]
    public void WithoutAPost_TheNearestIdleFriendlyGuard_Answers()
    {
        // vmangos GuardMgr::SummonGuard: no post for the area -> Creature::CallNearestGuard (50 yd, alive, idle, a guard, friendly, in sight).
        using Town t = Start(areaId: NoPostArea, more: [Spawn(2, StormwindGuardEntry, 30, 0), Spawn(3, StormwindGuardEntry, 45, 0)],
            moreTemplates: []);
        Creature near = t.Guards.Single(g => g.X == 30);
        Creature farther = t.Guards.Single(g => g.X == 45);
        (Player orc, _) = Horde(t.World, 1, 10, 0);

        t.Civilian.AI!.MoveInLineOfSight(orc);

        Assert.Same(orc, near.Combat.Victim);
        Assert.Null(farther.Combat.Victim);
        Assert.Equal(2, t.Guards.Count()); // nothing summoned
    }

    [Fact]
    public void EnteringCombat_CallsTheGuardsToo()
    {
        using Town t = Start();
        (Player orc, _) = Horde(t.World, 1, 30, 0); // out of sight range: only the fight calls

        t.Map.Combat.DealDamage(orc, t.Civilian, 1, direct: false);

        Creature guard = Assert.Single(t.Guards);
        Assert.Same(orc, guard.Combat.Victim);
    }

    /// <summary>
    /// vmangos GuardMgr::GetTeam (GuardMgr.cpp:403-418): against an enemy no player controls, the post sends the guard of the civilian's own
    /// team (Unit::GetTeam: Faction.dbc's team field, Unit.cpp:4960-4973). Without that seam the team is unknown: the charge is spent and
    /// nobody comes, as with vmangos TEAM_NONE.
    /// </summary>
    [Fact]
    public void AnEnemyNoPlayerControls_BringsTheGuardOfTheCiviliansOwnTeam()
    {
        const uint MobEntry = 7102;
        CreatureTemplate mob = Template(MobEntry) with { Faction = 14 };

        using Town unknown = Start(more: [Spawn(2, MobEntry, 30, 0)], moreTemplates: [mob]);
        Creature raider = unknown.System.Creatures.Single(c => c.Template.Entry == MobEntry);
        unknown.Map.Combat.DealDamage(raider, unknown.Civilian, 1, direct: false);
        Assert.Empty(unknown.Guards);
        Assert.Equal(GuardPostTable.MaxCharges - 1, unknown.Posts.ChargesOf(Goldshire));

        using Town known = Start(more: [Spawn(2, MobEntry, 30, 0)], moreTemplates: [mob], teamOf: c => c.Template.Entry == CivilianEntry ? Team.Alliance : null);
        Creature attacker = known.System.Creatures.Single(c => c.Template.Entry == MobEntry);
        known.Map.Combat.DealDamage(attacker, known.Civilian, 1, direct: false);
        Creature guard = Assert.Single(known.Guards);
        Assert.Same(attacker, guard.Combat.Victim);
    }

    [Fact]
    public void TheGuard_GoesAwayAfterTwoMinutes()
    {
        using Town t = Start();
        (Player orc, _) = Horde(t.World, 1, 10, 0);
        t.Civilian.AI!.MoveInLineOfSight(orc);
        Assert.Single(t.Guards);
        t.World.RemovePlayer(orc);

        Run(t.World, GuardPostTable.GuardDespawnMs + 1000, step: 1000);

        Assert.Empty(t.Guards);
    }

    /// <summary>
    /// vmangos summons the guard TEMPSUMMON_TIMED_OR_DEAD_DESPAWN (GuardMgr.cpp:451; Objects/TemporarySummon.cpp:127-148): its 2-minute
    /// timer counts only while it is alive and out of combat, and starts again from 2 minutes while it fights. A guard still fighting at
    /// 2 minutes stays; once the fight ends it stays another 2 minutes.
    /// </summary>
    [Fact]
    public void AGuardStillFightingAtTwoMinutes_Stays_AndGoesTwoMinutesAfterItsFightEnds()
    {
        using Town t = Start();
        (Player orc, _) = Horde(t.World, 1, 10, 0);
        orc.MaxHealth = 10_000_000;
        orc.Health = 10_000_000;
        t.Civilian.AI!.MoveInLineOfSight(orc);
        Creature guard = Assert.Single(t.Guards);

        Run(t.World, GuardPostTable.GuardDespawnMs + 30_000, step: 1000);

        Assert.True(orc.IsAlive);
        Assert.Same(guard, Assert.Single(t.Guards));
        Assert.True(guard.Combat.IsInCombat);
        Assert.Same(orc, guard.Combat.Victim);

        t.World.RemovePlayer(orc); // the fight ends 150 s after the summon
        Run(t.World, GuardPostTable.GuardDespawnMs - 10_000, step: 1000);
        Assert.False(guard.Combat.IsInCombat);
        Assert.Same(guard, Assert.Single(t.Guards)); // 260 s after the summon, 110 s after the fight

        Run(t.World, 15_000, step: 1000);
        Assert.Empty(t.Guards);
    }

    [Theory]
    [InlineData(362u, 0u, null, GuardPostTable.TextGuardOrc2)]        // Razor Hill always says "Grunts! Attack!"
    [InlineData(87u, 0u, 56u, GuardPostTable.TextGuardNightElf)]       // by model: a night elf female
    [InlineData(87u, 12u, 185u, GuardPostTable.TextGuardTroll)]        // the model wins over the faction
    [InlineData(87u, 12u, null, GuardPostTable.TextGuardHuman)]        // no metadata: the Stormwind faction template
    [InlineData(87u, 9999u, null, 0u)]                                  // nothing known: no text
    public void TheCallText_FollowsVmangosGetTextId(uint area, uint faction, uint? model, uint expected)
        => Assert.Equal(expected, GuardPostTable.GetTextId(faction, area, model));
}

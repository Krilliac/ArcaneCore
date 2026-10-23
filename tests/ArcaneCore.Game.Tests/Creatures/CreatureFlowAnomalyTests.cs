using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// The evade-loop, leash-reset-loop and waypoint-path-missing checks driven through the real flows: <see cref="CreatureMapSystem.EnterEvadeMode"/>,
/// the template's hard leash in the map tick, and a waypoint spawn with and without a path. Each scenario owns an entry (the ledger is process-wide).
/// </summary>
public sealed class CreatureFlowAnomalyTests
{
    private const uint EvadeEntry = 950001;
    private const uint SingleEvadeEntry = 950002;
    private const uint LeashEntry = 950003;
    private const uint SingleLeashEntry = 950004;
    private const uint NoPathEntry = 950005;
    private const uint PathEntry = 950006;
    private const uint ReasonEvadeEntry = 950007;
    private const uint LastVictimEvadeEntry = 950008;
    private const uint WonFightsEvadeEntry = 950009;
    private const uint FlaggedEvadeEntry = 950010;
    private const uint VictimEvadeEntry = 950011;
    private const uint LongEvadeEntry = 950012;
    private const uint ClearedGuidEvadeEntry = 950013;

    private sealed record Fight(WorldRuntime World, Map Map, CreatureMapSystem System, Player Player, Creature Wolf) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    private static Fight Start(uint entry, float leash = 0f)
    {
        CreatureContent content = Content([Template(entry) with { Leash = leash }], [Spawn(1, entry, 5, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        (Player player, _) = AddPlayer(runtime, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        wolf.AI!.CombatMovement = false;
        return new Fight(runtime, map, system, player, wolf);
    }

    private static AnomalyCapture Capture(string key, uint entry)
        => AnomalyLedger.Shared.Capture(n => n.Key == key && n.Subject.StartsWith($"creature entry {entry} ", StringComparison.Ordinal));

    /// <summary>One engine evade: pull the wolf out of place, evade, and let it walk home so the next evade is a new one.</summary>
    private static void EvadeOnce(Fight f)
    {
        f.Wolf.Relocate(25, 0, 83.5f, 0, 0);
        f.System.EnterEvadeMode(f.Wolf);
        Assert.True(f.Wolf.IsEvading);
        Run(f.World, 6_000);
        Assert.False(f.Wolf.IsEvading);
    }

    /// <summary>One hard-leash reset: start a fight, drag the wolf past the leash, and let the 3 s check evade it and the wolf walk home.</summary>
    private static void LeashOnce(Fight f)
    {
        f.Map.Combat.DealDamage(f.Player, f.Wolf, 1, direct: false);
        f.Wolf.Relocate(5 + 40, 0, 83.5f, 0, 0);
        Run(f.World, 3_100);
        Assert.True(f.Wolf.IsInEvadeMode, "the hard leash did not evade the wolf");
        Run(f.World, 8_000);
        Assert.False(f.Wolf.IsEvading);
    }

    [Fact]
    public void ThreeRealEvadesInTheWindow_ReportEvadeLoop_ExactlyOnce()
    {
        using AnomalyCapture capture = Capture("creature/evade-loop", EvadeEntry);
        using Fight f = Start(EvadeEntry);

        EvadeOnce(f);
        EvadeOnce(f);
        Assert.Empty(capture.Notices);

        EvadeOnce(f);
        Assert.Single(capture.Notices);

        EvadeOnce(f);
        Assert.Single(capture.Notices); // one report per episode
    }

    [Fact]
    public void TheEvadeLoopReport_NamesWhoSentTheCreatureHome_AndTheFightEachEvadeEnded()
    {
        using AnomalyCapture capture = Capture("creature/evade-loop", ReasonEvadeEntry);
        using Fight f = Start(ReasonEvadeEntry);

        EvadeOnce(f);
        EvadeOnce(f);
        // The third ends a real fight, the player its victim, and is asked by the AI.
        f.Wolf.Relocate(25, 0, 83.5f, 0, 0);
        f.Map.Combat.Attack(f.Wolf, f.Player, melee: false);
        f.Wolf.AI!.EnterEvadeMode();

        string detail = Assert.Single(capture.Notices).Detail;
        // Newest first: the AI's evade that triggered the report leads, then the second (the wolf, back home, had aggroed the player meanwhile).
        Assert.Contains("; evades, newest first: " + f.Wolf.AI!.GetType().Name + ".TheEvade", detail);
        Assert.Contains("; EvadeOnce(", detail);
    }

    /// <summary>
    /// Live 2026-10-18: almost every loop read "SelectHostileTarget(no victim, 0 att, Chase)" three times. An evade without a victim now names
    /// the last unit the creature attacked and its state (here a living player it stopped attacking).
    /// </summary>
    [Fact]
    public void AnEvadeWithoutAVictim_NamesTheLastVictim_AndItsState()
    {
        using AnomalyCapture capture = Capture("creature/evade-loop", LastVictimEvadeEntry);
        using Fight f = Start(LastVictimEvadeEntry);

        for (int i = 0; i < 3; i++)
        {
            f.Map.Combat.Attack(f.Wolf, f.Player, melee: false);
            f.Map.Combat.AttackStop(f.Wolf);
            EvadeOnce(f);
        }

        string detail = Assert.Single(capture.Notices).Detail;
        Assert.Contains("; evades, newest first: EvadeOnce(no victim, 0 att, ", detail);
        Assert.Contains(", last player alive ", detail);
    }

    /// <summary>
    /// Live 2026-10-22: Firemane mobs looped "SelectHostileTarget(no victim, 0 att)" every 2-3 s; something re-flagged combat without threat.
    /// Each evade of the report now names the member that last put the creature in combat and how often since the flag was cleared.
    /// </summary>
    [Fact]
    public void TheEvadeLoopReport_NamesWhoLastSetTheCreatureInCombat_AndHowOften()
    {
        using AnomalyCapture capture = Capture("creature/evade-loop", FlaggedEvadeEntry);
        using Fight f = Start(FlaggedEvadeEntry);

        string setBy = "";
        string setVia = "";
        uint sets = 0;
        for (int i = 0; i < 3; i++)
        {
            f.Map.Combat.DealDamage(f.Player, f.Wolf, 1, direct: false);
            f.Map.Combat.AttackStop(f.Wolf); // an evade without a victim, as the live loops
            setBy = f.Wolf.Combat.LastCombatSetBy ?? "";
            setVia = f.Wolf.Combat.LastCombatSetVia ?? "";
            sets = f.Wolf.Combat.CombatSetsSinceClear;
            EvadeOnce(f);
        }

        // The subsystem that flagged combat, not the helper it went through (which is named separately).
        Assert.Equal("DealDamage", setBy);
        Assert.Equal("SetInCombatWithAggressor", setVia);
        Assert.True(sets >= 1, $"sets={sets}");
        string detail = Assert.Single(capture.Notices).Detail;
        Assert.Contains($", by {setBy}/{setVia} x{sets}", detail);
    }

    /// <summary>
    /// Live 2026-10-18: six Stockade guards killed the four RP Defias Rioters one by one after each respawn, an evade after every kill (vmangos:
    /// the threat list is empty), and that was reported as an evade loop ~36 times an hour. Fights the creature won are no loop.
    /// </summary>
    [Fact]
    public void EvadesAfterKillingTheVictim_AreNoEvadeLoop()
    {
        using AnomalyCapture capture = Capture("creature/evade-loop", WonFightsEvadeEntry);
        using Fight f = Start(WonFightsEvadeEntry);
        f.Map.Combat.Attack(f.Wolf, f.Player, melee: false);
        f.Map.Combat.Kill(f.Wolf, f.Player);

        EvadeOnce(f);
        Assert.Empty(capture.Notices);
        for (int i = 0; i < 3; i++)
        {
            f.Wolf.Combat.LastVictimGuid = f.Player.Guid; // as after each guard's next kill (Attack refuses a dead victim)
            EvadeOnce(f);
        }

        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void ASingleRealEvade_ReportsNoEvadeLoop()
    {
        using AnomalyCapture capture = Capture("creature/evade-loop", SingleEvadeEntry);
        using Fight f = Start(SingleEvadeEntry);

        EvadeOnce(f);

        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void ThreeHardLeashResets_ReportLeashResetLoop_ExactlyOnce()
    {
        using AnomalyCapture capture = Capture("creature/leash-reset-loop", LeashEntry);
        using Fight f = Start(LeashEntry, leash: 30f);

        LeashOnce(f);
        LeashOnce(f);
        Assert.Empty(capture.Notices);

        LeashOnce(f);
        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("hard leash", notice.Detail);
    }

    [Fact]
    public void ASingleHardLeashReset_ReportsNoLeashResetLoop()
    {
        using AnomalyCapture capture = Capture("creature/leash-reset-loop", SingleLeashEntry);
        using Fight f = Start(SingleLeashEntry, leash: 30f);

        LeashOnce(f);

        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void AWaypointSpawnWithNoPath_ReportsWaypointPathMissing_OncePerSpawn()
    {
        using AnomalyCapture capture = Capture("creature/waypoint-path-missing", NoPathEntry);
        CreatureContent content = Content([Template(NoPathEntry)], [Spawn(1, NoPathEntry, 5, 0, movementType: 2)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            AddPlayer(world, 1, 0, 0);
            Run(world, 500);
            Creature wolf = Assert.Single(system.Creatures);
            Assert.NotEqual(MovementGeneratorType.Waypoint, wolf.Motion.CurrentType);

            AnomalyNotice notice = Assert.Single(capture.Notices);
            Assert.Contains("0 points", notice.Detail);
        }
    }

    [Fact]
    public void AWaypointSpawnWithAPath_ReportsNothing()
    {
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subject.StartsWith($"creature entry {PathEntry} ", StringComparison.Ordinal));
        var node = new CreatureWaypoint(1, 10, 0, 83.5f, 100f, 0);
        CreatureContent content = Content([Template(PathEntry)], [Spawn(1, PathEntry, 5, 0, movementType: 2)], waypoints: [(1u, node)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            AddPlayer(world, 1, 0, 0);
            Run(world, 500);

            Assert.Equal(MovementGeneratorType.Waypoint, Assert.Single(system.Creatures).Motion.CurrentType);
            Assert.Empty(capture.Notices);
        }
    }

    /// <summary>
    /// HighGuid.Player is 0, so an empty GUID read as a player: a scripted evade with no victim and the last-victim GUID already cleared (the
    /// previous evade clears it) was no longer exempt and looped into a report.
    /// </summary>
    [Fact]
    public void ScriptedEvadesWithNoVictimAndAClearedLastVictim_AreNoEvadeLoop()
    {
        using AnomalyCapture capture = Capture("creature/evade-loop", ClearedGuidEvadeEntry);
        using Fight f = Start(ClearedGuidEvadeEntry);

        for (int i = 0; i < 4; i++)
        {
            f.Wolf.Relocate(25, 0, 83.5f, 0, 0);
            f.Map.Combat.AttackStop(f.Wolf); // the wolf had re-aggroed the player on its way home
            f.Wolf.Combat.LastVictimGuid = default; // as after an evade
            Assert.Null(f.Wolf.Combat.Victim);
            f.System.EnterEvadeMode(f.Wolf, CreatureAnomalies.ScriptedEvadeReason);
            Assert.True(f.Wolf.IsEvading);
            Run(f.World, 6_000);
        }

        Assert.Empty(capture.Notices);
    }

    /// <summary>An evade that ended a fight with a victim names no flagging member: it is no "re-flagged without threat" case.</summary>
    [Fact]
    public void AnEvadeWithAVictim_CarriesNoFlaggedSuffix()
    {
        using AnomalyCapture capture = Capture("creature/evade-loop", VictimEvadeEntry);
        using Fight f = Start(VictimEvadeEntry);

        for (int i = 0; i < 3; i++)
        {
            f.Map.Combat.DealDamage(f.Player, f.Wolf, 1, direct: false);
            Assert.NotNull(f.Wolf.Combat.Victim);
            EvadeOnce(f);
        }

        string detail = Assert.Single(capture.Notices).Detail;
        Assert.Contains("(player alive ", detail);
        Assert.DoesNotContain(", by ", detail);
    }

    /// <summary>
    /// The ledger cuts a detail at 300 characters. Three evades without a victim, each with a long reason and the flagging suffix, do not fit:
    /// the report lists the newest evade first, so it is the oldest that is cut, never the evade that triggered the report.
    /// </summary>
    [Fact]
    public void TheEvadeLoopReport_KeepsTheNewestEvadeWhole_WhenTheTextIsCut()
    {
        using AnomalyCapture capture = Capture("creature/evade-loop", LongEvadeEntry);
        using Fight f = Start(LongEvadeEntry);

        foreach (string reason in new[] { "AlphaSelectHostileTargetReason", "BetaSelectHostileTargetReason", "GammaSelectHostileTargetReason" })
        {
            f.Map.Combat.DealDamage(f.Player, f.Wolf, 1, direct: false);
            f.Map.Combat.AttackStop(f.Wolf);
            f.Wolf.Relocate(25, 0, 83.5f, 0, 0);
            f.System.EnterEvadeMode(f.Wolf, reason);
            Run(f.World, 6_000);
        }

        string detail = Assert.Single(capture.Notices).Detail;
        Assert.EndsWith("...", detail); // the text was cut
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(detail, @"GammaSelectHostileTargetReason\(no victim, 0 att, threat \d+, \w+, last player alive \d+yd, by DealDamage/SetInCombatWithAggressor x\d+\)"), detail);
    }

    /// <summary>A guard (EventAI: ACTION_T_EVADE 100 ms into every fight) at x=5 and a dummy at x=8.</summary>
    private static CreatureContent EvadingGuardContent(uint guardEntry, uint dummyEntry)
        => new(
            [Template(guardEntry, t => t.AIName = CreatureAiFactory.EventAIName), Template(dummyEntry)],
            [Spawn(1, guardEntry, 5, 0), Spawn(2, dummyEntry, 8, 0)], [], [], [],
            new CreatureAiContent(
                [
                    new CreatureAiEvent
                    {
                        Id = 1,
                        CreatureId = guardEntry,
                        EventType = (byte)EventAiEventType.TimerInCombat,
                        Param1 = 100,
                        Param2 = 100,
                        Param3 = 100,
                        Param4 = 100,
                        Chance = 100,
                        Action1 = new CreatureAiAction((byte)EventAiActionType.Evade, 0, 0, 0),
                    },
                ],
                []));

    /// <summary>
    /// A scripted evade ends a fight on purpose, but not one against a player's pet: that is a player-driven fight, and a loop of it is a
    /// real defect (a guard that evades from a hunter's pet again and again).
    /// </summary>
    [Fact]
    public void ScriptedEventAiEvadesAgainstAPlayersPet_AreStillReportedAsEvadeLoop()
    {
        const uint GuardEntry = 948092;
        const uint PetEntry = 948093;
        using AnomalyCapture capture = Capture("creature/evade-loop", GuardEntry);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(EvadingGuardContent(GuardEntry, PetEntry));
        using (world)
        {
            (Player owner, _) = AddPlayer(world, 1, 0, 0);
            Run(world, 100);
            Creature guard = system.Creatures.Single(c => c.Entry == GuardEntry);
            Creature pet = system.Creatures.Single(c => c.Entry == PetEntry);
            pet.SetOwnerGuid(owner.Guid);

            for (int i = 0; i < 3; i++)
            {
                Assert.True(system.AttackStart(guard, pet), "the guard did not engage the pet");
                Run(world, 700); // the EventAI timers tick every 500 ms
                Assert.Null(guard.Combat.Victim);
                Run(world, 3_000);
            }

            Assert.Single(capture.Notices); // creature/evade-loop is reported
        }
    }

    [Fact]
    public void SparringCreaturesAndScriptedEventAiEvades_ClearHostileReferencesAndReportNoEvadeLoop()
    {
        const uint GuardEntry = 948090;
        const uint DummyEntry = 948091;
        using AnomalyCapture guardCapture = Capture("creature/evade-loop", GuardEntry);
        using AnomalyCapture dummyCapture = Capture("creature/evade-loop", DummyEntry);
        CreatureContent content = EvadingGuardContent(GuardEntry, DummyEntry);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            AddPlayer(world, 1, 0, 0);
            Run(world, 100);
            Creature guard = system.Creatures.Single(c => c.Entry == GuardEntry);
            Creature dummy = system.Creatures.Single(c => c.Entry == DummyEntry);

            for (int i = 0; i < 4; i++)
            {
                Assert.True(system.AttackStart(guard, dummy), "the guard did not engage the dummy"); // through the AI, so EventAI is in combat
                dummy.Combat.Threat.AddThreat(guard, 10f);
                Assert.True(dummy.Combat.HasThreatList);

                // The guard's EventAI row runs ACTION_T_EVADE (the production reason, not a literal) while sparring with the dummy.
                Assert.IsType<CreatureEventAI>(guard.AI);
                guard.Relocate(-20, 0, 83.5f, 0, 0); // far from home, so the guard is still running back when the dummy evades
                Run(world, 700); // the EventAI timers tick every 500 ms
                Assert.Null(guard.Combat.Victim); // the evade action ended the fight
                Assert.True(guard.IsInEvadeMode);
                Assert.False(dummy.Combat.HasThreatList);

                // Even if the dummy evades via SelectHostileTarget after its NPC opponent evaded:
                dummy.Combat.LastVictimGuid = guard.Guid;
                system.EnterEvadeMode(dummy, "SelectHostileTarget");
                Run(world, 8_000);
                Assert.False(guard.IsEvading);
            }

            Assert.Empty(guardCapture.Notices);
            Assert.Empty(dummyCapture.Notices);
            AnomalyCapture capture = dummyCapture;
            Assert.Equal(MovementGeneratorType.Idle, dummy.Motion.CurrentType);
            Assert.Empty(capture.Notices);
        }
    }
}

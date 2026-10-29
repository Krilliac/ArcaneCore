using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Tests.Docs;
using ArcaneCore.World.Tests.Honor;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// The warning lines of the bots and the creature scripts become signals. The fixtures follow the log templates of ManagedPlayerbotFeature and
/// the live values quoted in docs/integration (wave6: stall at -5608.8,-439.4; talents-bots: three deaths in 99 s, attackers=16422; the 2026-10-10
/// 200-bot run: set-aside for 30 min). No captured log file exists in the repository, so the contract tests build the strings with the real
/// <see cref="PlayerbotStallWatch"/> and <see cref="PlayerbotSharedSetAsides"/>.
/// </summary>
public sealed class PlayerbotQaSignalParserTests
{
    [Fact]
    public void Parses_LiveStallLine_GoalQuestTargetPositionMap()
    {
        string line = QaData.BotLine(QaData.Bot1, "Ironwander", "stalled 120s: goal=Quest target=6747 quest=1656 at -5608.8,-439.4,401.2 map 0");
        QaSignal s = QaData.Parse(line);

        Assert.Equal(QaSignalKind.Stall, s.Kind);
        Assert.Equal(120, s.Seconds);
        Assert.Equal("Quest", s.Goal);
        Assert.Equal(6747u, s.Target);
        Assert.Equal(1656u, s.Quest);
        Assert.True(s.HasPosition);
        Assert.Equal(-5608.8f, s.X);
        Assert.Equal(-439.4f, s.Y);
        Assert.Equal(401.2f, s.Z);
        Assert.Equal(0u, s.Map);
        Assert.Equal(QaData.Bot1, s.BotKey);
        Assert.DoesNotContain("Ironwander", s.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(QaData.Bot1.ToString(), s.Message, StringComparison.Ordinal);
        Assert.StartsWith("Playerbot <bot> stalled 120s: goal=Quest target=6747", s.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_LiveSharedSetAside_QuestForMinutes()
    {
        QaSignal quest = QaData.Parse("Playerbots: 3 bots stalled on quest 1656 for 30 min; every bot sets it aside");
        Assert.Equal(QaSignalKind.SharedSetAside, quest.Kind);
        Assert.Equal(3, quest.BotCount);
        Assert.Equal(1656u, quest.Quest);
        Assert.Equal(0u, quest.Target);
        Assert.Equal(30, quest.Minutes);
        Assert.Equal("Quest", quest.Goal);
        Assert.Null(quest.BotKey);

        QaSignal entry = QaData.Parse("Playerbots: 3 bots stalled on entry 3059 for 60 min; every bot sets it aside");
        Assert.Equal(3059u, entry.Target);
        Assert.Equal(0u, entry.Quest);
        Assert.Equal(60, entry.Minutes);
        Assert.Equal("Entry", entry.Goal);
    }

    [Fact]
    public void Parses_LiveDeathLoop_CountSpanPositionAttackers()
    {
        QaSignal s = QaData.Parse(QaData.BotLine(QaData.Bot2, "Dawnrover", "death loop: died 3 times in 99s within 60 yd of -5605.6,155.8,453.3 map 0 attackers=16422"));
        Assert.Equal(QaSignalKind.DeathLoop, s.Kind);
        Assert.Equal(3, s.Count);
        Assert.Equal(99, s.Seconds);
        Assert.Equal(-5605.6f, s.X);
        Assert.Equal(155.8f, s.Y);
        Assert.Equal(453.3f, s.Z);
        Assert.Equal(0u, s.Map);
        Assert.Equal(new uint[] { 16422 }, s.Attackers);
        Assert.Equal(QaData.Bot2, s.BotKey);
        Assert.DoesNotContain("Dawnrover", s.Message, StringComparison.Ordinal);

        // Several attackers: de-duplicated and sorted, so the order the bot met them in does not matter.
        QaSignal many = QaData.Parse(QaData.BotLine(QaData.Bot2, "Dawnrover", "death loop: died 4 times in 150s within 60 yd of 1.0,2.0,3.0 map 1 attackers=16422/2000/16422"));
        Assert.Equal(new uint[] { 2000, 16422 }, many.Attackers);
        Assert.Equal(1u, many.Map);

        QaSignal none = QaData.Parse(QaData.BotLine(QaData.Bot2, "Dawnrover", "death loop: died 3 times in 60s within 60 yd of 1.0,2.0,3.0 map 0 attackers=none"));
        Assert.Empty(none.Attackers);
    }

    [Fact]
    public void Parses_ActionFault_RecoveryCodeAndExceptionType()
    {
        // The template is "Playerbot {BotId} {What} failed ({Type}: {Message})" and the recovery throws InvalidOperationException(PlayerbotRecovery.Stalled).
        string line = $"Playerbot {QaData.Bot1} action failed (InvalidOperationException: {PlayerbotRecovery.Stalled})";
        QaSignal s = QaData.Parse(line);
        Assert.Equal(QaSignalKind.ActionFault, s.Kind);
        Assert.Equal("InvalidOperationException", s.ExceptionType);
        Assert.Equal("playerbot-recovery-stalled", s.FaultCode);
        Assert.Equal("action", s.Goal);
        Assert.Equal(QaData.Bot1, s.BotKey);

        QaSignal other = QaData.Parse($"Playerbot {QaData.Bot1} movement failed (NullReferenceException: Object reference not set to an instance of an object.)");
        Assert.Equal("NullReferenceException", other.ExceptionType);
        Assert.Equal(string.Empty, other.FaultCode);
        Assert.Equal("movement", other.Goal);
    }

    [Fact]
    public void Parses_Quarantine_FaultNofM()
    {
        string line = $"Playerbot {QaData.Bot1} quarantined for 30 s (fault 2 of 5): action: {PlayerbotRecovery.SpiritHealerFailed}";
        QaSignal s = QaData.Parse(line);
        Assert.Equal(QaSignalKind.Quarantine, s.Kind);
        Assert.Equal(2, s.Count);
        Assert.Equal("playerbot-recovery-spirit-healer-failed", s.FaultCode);
        Assert.Contains("action", s.Detail, StringComparison.Ordinal);
        Assert.Equal(QaData.Bot1, s.BotKey);
    }

    [Fact]
    public void Parses_Disabled_AfterRepeatedFaults()
    {
        QaSignal s = QaData.Parse($"Playerbot {QaData.Bot3} disabled after 5 faults within 600 s: action: {PlayerbotRecovery.Stalled}");
        Assert.Equal(QaSignalKind.Disabled, s.Kind);
        Assert.Equal(5, s.Count);
        Assert.Equal("playerbot-recovery-stalled", s.FaultCode);
    }

    [Fact]
    public void Parses_RelayUnsupported_MovementWaypointPathMissing()
    {
        QaSignal s = QaData.Parse("relay script 4021 uses unsupported MOVEMENT waypoint path 7 missing for entry 12345; that step is skipped");
        Assert.Equal(QaSignalKind.RelayDataMissing, s.Kind);
        Assert.Equal(4021u, s.Relay);
        Assert.Equal(7u, s.PathId);
        Assert.Equal(12345u, s.Entry);
        Assert.Equal("MOVEMENT", s.Detail);
        Assert.False(s.HasPosition);
        Assert.Null(s.BotKey);

        QaSignal spawn = QaData.Parse("relay script 9 uses unsupported TEMP_SPAWN_CREATURE path 3 missing for entry 99; that step is skipped");
        Assert.Equal("TEMP_SPAWN_CREATURE", spawn.Detail);
        Assert.Equal(3u, spawn.PathId);
        Assert.Equal(99u, spawn.Entry);

        // A step the server does not implement at all has no path and no entry.
        QaSignal other = QaData.Parse("relay script 5 uses unsupported SET_HOVER fly animation flag; that step is skipped");
        Assert.Equal(QaSignalKind.RelayDataMissing, other.Kind);
        Assert.Equal(0u, other.Entry);
        Assert.Equal("SET_HOVER fly animation flag", other.Detail);

        QaSignal missing = QaData.Parse("relay script 77 does not exist (dbscripts_on_relay); not started");
        Assert.Equal(QaSignalKind.RelayScriptMissing, missing.Kind);
        Assert.Equal(77u, missing.Relay);
    }

    [Fact]
    public void Parses_RelayFormationStep_AsRelayDataMissing()
    {
        // CreatureMapSystem.ReportFormationStep: command 51 (spawn group) formation subcommands the server cannot run.
        QaSignal s = QaData.Parse("relay script 4021 command 51 subcommand 3: formation member 12 has no leader; that step does nothing");
        Assert.Equal(QaSignalKind.RelayDataMissing, s.Kind);
        Assert.Equal(4021u, s.Relay);
        Assert.Equal("command 51 subcommand 3: formation member 12 has no leader", s.Detail);
        Assert.False(s.HasPosition);
        Assert.Null(s.BotKey);
    }

    [Fact]
    public void StallReport_FromRealStallWatch_RoundTrips()
    {
        Player player = NewPlayer(-5608.8f, -439.4f, 401.2f);
        var watch = new PlayerbotStallWatch();
        PlayerbotStallWatch.Fingerprint Same(Player p) => default;

        Assert.False(watch.Observe(player, PlayerbotGoalKind.Quest, 6747, 1656, 0, 120_000, Same));
        Assert.True(watch.Observe(player, PlayerbotGoalKind.Quest, 6747, 1656, 120_000, 120_000, Same));
        string report = Assert.IsType<string>(watch.Report);

        QaSignal s = QaData.Parse(QaData.BotLine(QaData.Bot1, "Ironwander", report));
        Assert.Equal(QaSignalKind.Stall, s.Kind);
        Assert.Equal(120, s.Seconds);
        Assert.Equal("Quest", s.Goal);
        Assert.Equal(6747u, s.Target);
        Assert.Equal(1656u, s.Quest);
        Assert.Equal(player.X, s.X, 0.06f);
        Assert.Equal(player.Y, s.Y, 0.06f);
        Assert.Equal(player.Z, s.Z, 0.06f);
        Assert.Equal(player.MapId, s.Map);
    }

    [Fact]
    public void DeathLoopReport_FromRealStallWatch_RoundTrips()
    {
        var watch = new PlayerbotStallWatch();
        var here = new Vector3(-5605.6f, 155.8f, 453.3f);
        Assert.False(watch.RecordDeath(1_000, 0, here, [16422]).Loop);
        Assert.False(watch.RecordDeath(50_000, 0, here + new Vector3(3, 2, 0), [16422, 2000]).Loop);
        Assert.True(watch.RecordDeath(100_000, 0, here, [16422]).Loop);
        string report = Assert.IsType<string>(watch.Report);

        QaSignal s = QaData.Parse(QaData.BotLine(QaData.Bot2, "Dawnrover", report));
        Assert.Equal(QaSignalKind.DeathLoop, s.Kind);
        Assert.Equal(3, s.Count);
        Assert.Equal(99, s.Seconds);
        Assert.Equal(new uint[] { 2000, 16422 }, s.Attackers);
        Assert.Equal(here.X, s.X, 0.06f);
        Assert.Equal(here.Y, s.Y, 0.06f);
        Assert.Equal(0u, s.Map);
    }

    [Fact]
    public void SharedSetAsideReport_FromRealSharedSetAsides_RoundTrips()
    {
        var shared = new PlayerbotSharedSetAsides();
        string? everyone = null;
        for (ulong bot = 1; bot <= PlayerbotSharedSetAsides.BotsToShare; bot++)
        {
            everyone = shared.Report(PlayerbotGoalKind.Quest, 6747, 1656, bot, (uint)(bot * 1000))?.Goal;
        }

        // ManagedPlayerbotFeature: "Playerbots: {Count} bots stalled on {Goal}; every bot sets it aside (stalls at {Stalls})".
        QaSignal s = QaData.Parse($"Playerbots: {PlayerbotSharedSetAsides.BotsToShare} bots stalled on {everyone}; every bot sets it aside");
        Assert.Equal(QaSignalKind.SharedSetAside, s.Kind);
        Assert.Equal(1656u, s.Quest);
        Assert.Equal(30, s.Minutes);
        Assert.Equal(PlayerbotSharedSetAsides.BotsToShare, s.BotCount);
    }

    [Fact]
    public void ProductionLogTemplates_StillMatchTheLinesTheseTestsWrap()
    {
        // The templates are literals in ManagedPlayerbotFeature and CreatureMapSystem; rewording one must fail here and not blind QA silently.
        string feature = RepoRoot.ReadText("src/ArcaneCore.Playerbots/ManagedPlayerbotFeature.cs");
        Assert.Contains("\"Playerbot {BotId} ({Name}) {Stall}; it gives up that goal\"", feature, StringComparison.Ordinal);
        Assert.Contains("\"Playerbot {BotId} ({Name}) {Stall}" + PlayerbotQaSignalParser.OutsideHomeSuffix + "\"", feature, StringComparison.Ordinal);
        Assert.Contains("\"Playerbots: {Count} bots stalled on {Goal}; every bot sets it aside (stalls at {Stalls})\"", feature, StringComparison.Ordinal);
        Assert.Contains("\"Playerbot {BotId} {What} failed ({Type}: {Message})\"", feature, StringComparison.Ordinal);
        Assert.Contains("\"Playerbot {BotId} quarantined for {Seconds} s (fault {Count} of {Max}): {Fault}\"", feature, StringComparison.Ordinal);
        Assert.Contains("\"Playerbot {BotId} disabled after {Count} faults within {Window} s: {Fault}\"", feature, StringComparison.Ordinal);

        string relay = RepoRoot.ReadText("src/ArcaneCore.Game/Creatures/CreatureMapSystem.RelayScripts.cs");
        Assert.Contains("\"relay script {Relay} uses unsupported {What}; that step is skipped\"", relay, StringComparison.Ordinal);
        Assert.Contains("\"relay script {Relay} does not exist (dbscripts_on_relay); not started\"", relay, StringComparison.Ordinal);
        string formation = RepoRoot.ReadText("src/ArcaneCore.Game/Creatures/CreatureMapSystem.RelayFormation.cs");
        Assert.Contains("\"relay script {Relay} command 51 subcommand {Sub}: {What}; that step does nothing\"", formation, StringComparison.Ordinal);
        string commands = RepoRoot.ReadText("src/ArcaneCore.Game/Creatures/CreatureMapSystem.RelayCommands.cs");
        Assert.Contains("$\"MOVEMENT waypoint path {step.DataLong2} missing for entry {mover.Template.Entry}\"", commands, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Managed playerbot start failed (InvalidOperationException: nope)")]
    [InlineData("Playerbot 3f2b1c9e-0a4d-4e1b-9c55-7d1a2b3c4d5e (Ironwander) is doing something else")]
    [InlineData("Playerbots: 3 bots stalled on quest abc for 30 min; every bot sets it aside")]
    [InlineData("relay script abc does not exist")]
    [InlineData("relay script 12 was started")]
    [InlineData("stalled 120s: goal=Quest target=1 quest=2 at 1.0,2.0,3.0 map 0")]
    public void UnrelatedWarning_IsIgnored(string? line)
    {
        Assert.False(PlayerbotQaSignalParser.TryParse(line, QaData.T0, null, out QaSignal? signal));
        Assert.Null(signal);
    }

    [Theory]
    [InlineData("Playerbot 3f2b1c9e-0a4d-4e1b-9c55-7d1a2b3c4d5e (X) stalled 120s: goal=Quest target=99999999999 quest=1 at 1.0,2.0,3.0 map 0")]
    [InlineData("Playerbot 3f2b1c9e-0a4d-4e1b-9c55-7d1a2b3c4d5e (X) stalled 120s: goal=Quest target=1 quest=1 at 1.0,2.0 map 0")]
    [InlineData("Playerbot 3f2b1c9e-0a4d-4e1b-9c55-7d1a2b3c4d5e (X) death loop: died 3 times in 99s within 60 yd of 1.0,2.0,3.0 map 0 attackers=1/99999999999")]
    public void Malformed_OrOverflowingNumbers_AreRejected(string line)
    {
        Assert.False(PlayerbotQaSignalParser.TryParse(line, QaData.T0, null, out QaSignal? signal));
        Assert.Null(signal);
    }

    [Fact]
    public void OversizedLine_IsRejected_AndLongFaultTextIsCapped()
    {
        string huge = "Playerbot " + QaData.Bot1 + " action failed (InvalidOperationException: " + new string('x', 5000) + ")";
        Assert.False(PlayerbotQaSignalParser.TryParse(huge, QaData.T0, null, out _));

        string longFault = "Playerbot " + QaData.Bot1 + " action failed (InvalidOperationException: " + new string('y', 3000) + ")";
        QaSignal s = QaData.Parse(longFault);
        Assert.True(s.Message.Length <= PlayerbotQaSignalParser.MaxMessageChars);
        Assert.True(s.Detail.Length <= PlayerbotQaSignalParser.MaxMessageChars);
    }

    [Fact]
    public void BotKey_FallsBackToTheLogState_WhenTheTextIdIsNotAGuid()
    {
        QaSignal s = QaData.Parse("Playerbot 42 (X) stalled 120s: goal=Grind target=1196 quest=0 at 1.0,2.0,3.0 map 0", QaData.Bot3);
        Assert.Equal(QaData.Bot3, s.BotKey);
    }

    /// <summary>
    /// The feature logs a goal set aside for every bot when the brain reports it, not only next to a new stall line. The brain gives its
    /// goal up again after every further stall bound (<see cref="PlayerbotStallWatch.GiveUps"/>) without a new stall to log, and such a
    /// give-up can complete a shared set-aside: live 2026-10-11 04:23:57, <c>3 bots stalled on quest 2160 for 60 min</c> was logged beside
    /// an Explore stall of another quest, and no stall on quest 2160 had been logged in the 30 minutes before it.
    /// </summary>
    [Fact]
    public void LogStall_LogsASharedSetAside_WithoutANewStall_AndBothLinesParse()
    {
        var log = new CapturingLogger();
        var shared = new PlayerbotSharedSetAsides();
        shared.Report(PlayerbotGoalKind.Quest, 0, 2160, 1, 1_000, 0, new Vector3(-9540.1f, 1015.2f, 52.3f));
        shared.Report(PlayerbotGoalKind.Quest, 0, 2160, 2, 2_000, 0, new Vector3(-7527.0f, -2114.0f, 130.0f));
        PlayerbotSharedSetAside? everyone = shared.Report(PlayerbotGoalKind.Quest, 6806, 2160, 3, 3_000, 0, new Vector3(-10275.0f, 1365.0f, 41.0f));
        Assert.NotNull(everyone);

        // A further give-up of a stall already logged: no stall line, but the set-aside is logged at once.
        (int logged, bool sharedLogged) = ManagedPlayerbotFeature.LogStall(log, QaData.Bot1, "Wfbotcg", stallCount: 4, stallsLogged: 4,
            "stalled 360s: goal=Quest target=6806 quest=2160 at -10275.0,1365.0,41.0 map 0", outsideHome: false, everyone);
        Assert.Equal(4, logged);
        Assert.True(sharedLogged);
        QaSignal s = QaData.Parse(Assert.Single(log.Lines));
        Assert.Equal(QaSignalKind.SharedSetAside, s.Kind);
        Assert.Equal(2160u, s.Quest);
        Assert.Equal(30, s.Minutes);
        Assert.True(s.HasPosition); // the first stall behind it
        Assert.Equal(-9540.1f, s.X);
        Assert.Equal(1015.2f, s.Y);
        Assert.Equal(0u, s.Map);
        Assert.Contains("-7527.0,-2114.0,130.0 map 0", s.Message, StringComparison.Ordinal);

        // A new stall outside the home is logged as such and parsed as such.
        log.Lines.Clear();
        (logged, sharedLogged) = ManagedPlayerbotFeature.LogStall(log, QaData.Bot1, "Wfbotcg", stallCount: 5, stallsLogged: 4,
            "stalled 120s: goal=Quest target=1376 quest=400 at -8285.0,554.0,101.0 map 0", outsideHome: true, everyone: null);
        Assert.Equal(5, logged);
        Assert.False(sharedLogged);
        QaSignal outside = QaData.Parse(Assert.Single(log.Lines));
        Assert.Equal(QaSignalKind.Stall, outside.Kind);
        Assert.True(outside.OutsideHome);
        Assert.Equal(400u, outside.Quest);

        // The same stall inside the home is an ordinary stall line.
        log.Lines.Clear();
        ManagedPlayerbotFeature.LogStall(log, QaData.Bot1, "Wfbotcg", 6, 5,
            "stalled 120s: goal=Quest target=1376 quest=400 at -8285.0,554.0,101.0 map 0", outsideHome: false, everyone: null);
        Assert.False(QaData.Parse(Assert.Single(log.Lines)).OutsideHome);
        Assert.EndsWith("; it gives up that goal", log.Lines[0], StringComparison.Ordinal);
    }

    private const string QaContext = " qa[level=10,race=Human,team=Alliance,class=Warrior,goal=Quest,quest=400,graveyard=0,distance=unknown,source=None,attackerFacts=none]";

    private static QaSignal LoggedStall(string report)
    {
        var log = new CapturingLogger();
        ManagedPlayerbotFeature.LogStall(log, QaData.Bot1, "Wfbotcg", 1, 0, report, outsideHome: false, everyone: null, QaContext);
        return QaData.Parse(Assert.Single(log.Lines));
    }

    [Fact]
    public void TheReturnTripMark_IsReadOnlyImmediatelyBeforeTheFirstQaContext()
    {
        const string stall = "stalled 120s: goal=Quest target=1376 quest=400 at -8285.0,554.0,101.0 map 0";
        Assert.True(LoggedStall(stall + PlayerbotQaSignalParser.ReturnTripToken).ReturnTrip);
        Assert.False(LoggedStall(stall).ReturnTrip);
        // The token somewhere in the body, not directly before the qa context.
        Assert.False(LoggedStall(stall + " return-trip at the ender").ReturnTrip);
        // The words after the first qa context (an attacker list, a fault text) never mark a stall.
        Assert.False(QaData.Parse(QaData.BotLine(QaData.Bot1, "Wfbotcg", stall + QaContext + " return-trip qa[x]")).ReturnTrip);
    }

    /// <summary>
    /// The outside-home mark belongs to a living stall's line. A death loop also counts as a stall and its report comes first
    /// (<see cref="PlayerbotBrain.StallReport"/>), while the brain's flag still holds the last give-up: the death loop is logged without the
    /// mark (review of #145).
    /// </summary>
    [Fact]
    public void LogStall_ADeathLoop_IsNeverMarkedOutsideTheHome()
    {
        var log = new CapturingLogger();
        ManagedPlayerbotFeature.LogStall(log, QaData.Bot1, "Wfbotfk", stallCount: 2, stallsLogged: 1,
            "death loop: died 3 times in 99s within 60 yd of -10650.0,400.0,35.0 map 0 attackers=202/531/569", outsideHome: true, everyone: null);
        string line = Assert.Single(log.Lines);
        Assert.EndsWith("; it gives up that goal", line, StringComparison.Ordinal);
        QaSignal s = QaData.Parse(line);
        Assert.Equal(QaSignalKind.DeathLoop, s.Kind);
        Assert.False(s.OutsideHome);
    }

    /// <summary>A logger that keeps the formatted warning lines.</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        internal List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning) Lines.Add(formatter(state, exception));
        }
    }

    private static Player NewPlayer(float x, float y, float z)
    {
        var character = new CharacterRecord { Id = 1, AccountId = 1, Name = "Ranked", Race = 1, Class = 1, Gender = 0, Level = 10, MapId = 0, ZoneId = 12, X = x, Y = y, Z = z };
        var appearance = new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400);
        return new Player(character, appearance, new NullPlayerSession());
    }
}

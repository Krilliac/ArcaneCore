using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

/// <summary>
/// EventAI's START_RELAY_SCRIPT (53) and the relay DB scripts it runs (cmangos CreatureEventAI.cpp:1227-1247; Map::ScriptsStart /
/// ScriptsProcess, Maps/Map.cpp:2166-2272; ScriptAction, DBScripts/ScriptMgr.cpp). The first test replays classic-db's Elly Langston
/// (creature 1328, relays 19958-19964): a player's wave starts a relay that pauses her patrol, turns her to the player, plays an emote
/// and a random line, and walks on 4 s later. Rows and ids are synthetic.
/// </summary>
public sealed class RelayScriptTests
{
    private const uint TextEmoteWave = 101;
    private const uint Relay = 919958;
    private const uint StringTemplate = 27;
    private const uint Greeting = 919100;
    private const uint BuddyEntry = 7201;

    private static RelayScriptStep Step(uint id, uint delay, uint command, uint dataLong = 0, uint flags = 0, uint buddy = 0, uint radius = 0,
        int dataInt = 0, uint dataLong2 = 0, float x = 0, float y = 0, float z = 0, float o = 0)
        => new(id, delay, 0, command, dataLong, dataLong2, 0, buddy, radius, flags, dataInt, 0, 0, 0, 0, x, y, z, o, 0, 0);

    private static CreatureAiEvent WaveRow(int relay)
        => new()
        {
            Id = 1,
            CreatureId = WolfEntry,
            EventType = 22,
            Flags = 1,
            Param1 = (int)TextEmoteWave,
            Action1 = new CreatureAiAction((byte)EventAiActionType.StartRelayScript, relay, 7, 0), // target 7: the invoker('s owner)
        };

    private sealed record Town(WorldRuntime World, Map Map, CreatureMapSystem System, Creature Elly, Player Player, FakeSession Session, FakeCaster Spells)
        : IDisposable
    {
        public void Dispose() => World.Dispose();

        public uint EmoteState => Elly.GetUInt32(UpdateFields.UnitNpcEmotestate);
    }

    private static Town Start(IEnumerable<RelayScriptStep> steps, int relay = (int)Relay, IEnumerable<RelayScriptTemplateChoice>? relayTemplates = null,
        IEnumerable<CreatureSpawn>? more = null, bool patrol = true)
    {
        var texts = new BroadcastTextCatalog([new BroadcastText(Greeting, "Ooh, a dashing $N!", "", 0, 7, 0, [], [])]);
        var ai = new CreatureAiContent([WaveRow(relay)], [], texts, textTemplates: [new CreatureAiTextChoice(StringTemplate, (int)Greeting, 0)])
        {
            RelayScripts = new RelayScriptCatalog(steps, relayTemplates ?? []),
        };
        CreatureContent content = new(
            [Template() with { AIName = CreatureAiFactory.EventAIName, Civilian = true }, Template(BuddyEntry) with { Civilian = true }],
            [Spawn(1, WolfEntry, 0, 0, movementType: (byte)(patrol ? 2 : 0)), .. more ?? []],
            patrol ? [(1u, new CreatureWaypoint(1, 30, 0, 83.5f, 100, 0)), (1u, new CreatureWaypoint(2, 0, 0, 83.5f, 100, 0))] : [], [], [], ai);
        var spells = new FakeCaster();
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = spells });
        (Player player, FakeSession session) = AddPlayer(world, 1, 0, 10);
        Creature elly = system.Creatures.Single(c => c.Template.Entry == WolfEntry);
        return new Town(world, map, system, elly, player, session, spells);
    }

    private static IEnumerable<RelayScriptStep> Elly() =>
    [
        Step(Relay, 0, 32, dataLong: 1, flags: 2),            // pause waypoints
        Step(Relay, 1000, 36, flags: 2),                      // face the player
        Step(Relay, 1101, 1, dataLong: 10, flags: 2),         // EMOTE_STATE_DANCE
        Step(Relay, 1101, 0, dataLong: StringTemplate, flags: 2), // a line from string template 27
        Step(Relay, 4000, 32, dataLong: 0, flags: 2),         // unpause
    ];

    [Fact]
    public void AWave_StartsTheRelay_SheStops_TurnsDancesAndSpeaks_ThenWalksOn()
    {
        using Town t = Start(Elly());
        Run(t.World, 500);
        Assert.True(t.Elly.IsMoving); // on her patrol
        t.Session.Clear();

        t.Elly.ReceiveEmote(t.Player, TextEmoteWave);

        Assert.False(t.Elly.IsMoving);  // PAUSE_WAYPOINTS ran at once (no delay)
        (float x, float y) = (t.Elly.X, t.Elly.Y);
        Run(t.World, 1200);
        Assert.Equal((x, y), (t.Elly.X, t.Elly.Y));
        float toPlayer = Creature.NormalizeOrientation(MathF.Atan2(t.Player.Y - t.Elly.Y, t.Player.X - t.Elly.X));
        Assert.Equal(toPlayer, t.Elly.Orientation, 0.01f);
        Assert.Equal(10u, t.EmoteState);
        MonsterChat said = ParseMonsterChat(Assert.Single(Packets(t.Session, WorldOpcode.SmsgMessagechat)));
        Assert.Equal("Ooh, a dashing P1!", said.Message);

        Run(t.World, 2700); // 3.9 s
        Assert.False(t.Elly.IsMoving);
        Run(t.World, 1500); // past 4 s: unpaused, back on the path
        Assert.True(t.Elly.IsMoving);
        Assert.Equal(0, t.System.PendingRelaySteps);
    }

    [Fact]
    public void TheSameRelay_DoesNotStartTwice_ForTheSamePair_WhileItRuns()
    {
        using Town t = Start(Elly());
        t.Elly.ReceiveEmote(t.Player, TextEmoteWave);
        int pending = t.System.PendingRelaySteps;
        Assert.Equal(4, pending); // the undelayed pause ran, four steps wait

        t.Elly.ReceiveEmote(t.Player, TextEmoteWave);

        Assert.Equal(pending, t.System.PendingRelaySteps); // SCRIPT_EXEC_PARAM_UNIQUE_BY_SOURCE_TARGET
        Run(t.World, 4100);
        t.Elly.ReceiveEmote(t.Player, TextEmoteWave);
        Assert.Equal(pending, t.System.PendingRelaySteps); // over: it may start again
    }

    [Fact]
    public void TerminateScript_EndsTheRun_WhenItsNpcIsMissing_OrWithAdditional_WhenItIsThere()
    {
        // Without COMMAND_ADDITIONAL: the script ends when no living npc of the entry is within datalong2 yd.
        using Town missing = Start([Step(Relay, 0, 31, dataLong: BuddyEntry, dataLong2: 20, flags: 2), Step(Relay, 100, 1, dataLong: 10, flags: 2)], patrol: false);
        missing.Elly.ReceiveEmote(missing.Player, TextEmoteWave);
        Run(missing.World, 300);
        Assert.Equal(0u, missing.EmoteState);

        using Town present = Start([Step(Relay, 0, 31, dataLong: BuddyEntry, dataLong2: 20, flags: 2), Step(Relay, 100, 1, dataLong: 10, flags: 2)],
            more: [Spawn(2, BuddyEntry, 5, 0)], patrol: false);
        present.Elly.ReceiveEmote(present.Player, TextEmoteWave);
        Run(present.World, 300);
        Assert.Equal(10u, present.EmoteState);

        using Town additional = Start([Step(Relay, 0, 31, dataLong: BuddyEntry, dataLong2: 20, flags: 2 | 8), Step(Relay, 100, 1, dataLong: 10, flags: 2)],
            more: [Spawn(2, BuddyEntry, 5, 0)], patrol: false);
        additional.Elly.ReceiveEmote(additional.Player, TextEmoteWave);
        Run(additional.World, 300);
        Assert.Equal(0u, additional.EmoteState);
    }

    [Fact]
    public void ABuddy_TakesTheSourceSeat_OrTheTargetSeatWithBuddyAsTarget()
    {
        // Source = the player (the EventAI target), target = Elly; a buddy found around her acts instead of the source.
        // The search runs around the source (the player at (0, 10)); the buddy stands 10.8 yd from it.
        using Town t = Start([Step(Relay, 0, 1, dataLong: 13, buddy: BuddyEntry, radius: 12)], more: [Spawn(2, BuddyEntry, 4, 0)], patrol: false);
        Creature buddy = t.System.Creatures.Single(c => c.Template.Entry == BuddyEntry);

        t.Elly.ReceiveEmote(t.Player, TextEmoteWave);

        Assert.Equal(13u, buddy.GetUInt32(UpdateFields.UnitNpcEmotestate)); // EMOTE_STATE_SIT on the buddy
        Assert.Equal(0u, t.EmoteState);

        using Town far = Start([Step(Relay, 0, 1, dataLong: 13, buddy: BuddyEntry, radius: 3), Step(Relay, 0, 1, dataLong: 10, flags: 2)],
            more: [Spawn(2, BuddyEntry, 4, 0)], patrol: false);
        far.Elly.ReceiveEmote(far.Player, TextEmoteWave);
        Assert.Equal(0u, far.System.Creatures.Single(c => c.Template.Entry == BuddyEntry).GetUInt32(UpdateFields.UnitNpcEmotestate)); // out of range: skipped
        Assert.Equal(10u, far.EmoteState); // and the script went on
    }

    [Fact]
    public void ANegativeRelayId_IsARelayTemplate()
    {
        using Town t = Start([Step(Relay, 0, 1, dataLong: 10, flags: 2), Step(Relay + 1, 0, 1, dataLong: 13, flags: 2)], relay: -39,
            relayTemplates: [new RelayScriptTemplateChoice(39, Relay + 1, 100)], patrol: false);

        t.Elly.ReceiveEmote(t.Player, TextEmoteWave);

        Assert.Equal(13u, t.EmoteState);
    }

    [Fact]
    public void MoveTo_WalksToThePoint_AndStartsItsArrivalRelay_ThenAOneShotEmoteIsSentNotKept()
    {
        using Town t = Start([Step(Relay, 0, 3, dataLong: Relay + 1, flags: 2, x: 8, y: 0, z: 83.5f), Step(Relay + 1, 0, 1, dataLong: 5)], patrol: false);
        t.Session.Clear();

        t.Elly.ReceiveEmote(t.Player, TextEmoteWave);
        Assert.True(t.Elly.IsMoving);
        Run(t.World, 6000);

        Assert.Equal(8f, t.Elly.X, 1);
        Assert.Equal(0u, t.EmoteState); // emote 5 (EXCLAMATION) is a one-shot
        Assert.Contains(Packets(t.Session, WorldOpcode.SmsgEmote), p => BitConverter.ToUInt32(p, 0) == 5);
    }

    [Fact]
    public void CastSpell_CastsAtTheTarget_AndAnUnsupportedCommand_IsSkipped()
    {
        using Town t = Start([Step(Relay, 0, 15, dataLong: 4321, flags: 2), Step(Relay, 0, 6, flags: 2), Step(Relay, 0, 1, dataLong: 10, flags: 2)], patrol: false);

        t.Elly.ReceiveEmote(t.Player, TextEmoteWave);

        (uint spell, Unit? target, bool triggered) = Assert.Single(t.Spells.Casts);
        Assert.Equal((4321u, false), (spell, triggered));
        Assert.Same(t.Player, target);
        Assert.Equal(10u, t.EmoteState); // TELEPORT_TO (6) was skipped, the next step ran
    }
}

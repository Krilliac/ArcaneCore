using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Time;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;

namespace ArcaneCore.World.Gm.DebugTools;

/// <summary>
/// <c>.wiresample &lt;name&gt;</c>: send the invoker one sample of a server packet no gameplay flow of ArcaneCore produces (ArcaneCore only; vmangos has no
/// such command), so a real 1.12.1 client can be watched for what it does with it (docs/areas/protocol-decisions.md lists the check). The bodies come from
/// <see cref="ProtocolGapPackets"/>, whose layouts are the 1.12.1 client's own readers. Samples of a packet the stock client has no handler for
/// (<c>npc-wont-talk</c>, <c>ghostee-gone</c>, <c>chat-player-ambiguous</c>, <c>script-message</c>, <c>gametimebias-set</c>, <c>character-profile</c>,
/// <c>character-profile-realm-connected</c>, <c>minigame-move-failed</c>) are dropped by a stock client: they exercise the server send path only. The
/// spline, compressed-move, game-time, game-speed, mini-game and clear-far-sight samples can change what the invoker's client shows until its next
/// update; nothing server-side changes. Administrator only; a console invoker has no client and is refused.
/// Session task on the world thread (it runs as a chat command); allocates one small array per use.
/// </summary>
public sealed class WireSampleCommand : ICommandGroup
{
    private delegate (WorldOpcode Opcode, byte[] Body) Sample(WorldSession session, ulong self);

    private static readonly SortedDictionary<string, Sample> Samples = new(StringComparer.Ordinal)
    {
        ["chat-restricted"] = (_, _) => (WorldOpcode.SmsgChatRestricted, ProtocolGapPackets.ChatRestricted()),
        ["play-time-warning"] = (_, _) => (WorldOpcode.SmsgPlayTimeWarning, ProtocolGapPackets.PlayTimeWarning(0x1000, 600)), // vmangos PTF_APPROACHING_PARTIAL_PLAY_TIME
        ["expected-spam-records"] = (_, _) => (WorldOpcode.SmsgExpectedSpamRecords, ProtocolGapPackets.ExpectedSpamRecords(["wiresample"])),
        ["spline-swim-start"] = (_, self) => (WorldOpcode.SmsgSplineMoveStartSwim, ProtocolGapPackets.SplineMoveSwim(self)),
        ["spline-swim-stop"] = (_, self) => (WorldOpcode.SmsgSplineMoveStopSwim, ProtocolGapPackets.SplineMoveSwim(self)),
        ["compressed-moves"] = (_, self) => (WorldOpcode.SmsgCompressedMoves, ProtocolGapPackets.CompressedRunSpeedMove(self, 7f)),
        ["loot-item-notify"] = (_, self) => (WorldOpcode.SmsgLootItemNotify, ProtocolGapPackets.LootItemNotify(self, 1, 1, 6948, "wiresample")),
        ["standstate-change-failure"] = (_, _) => (WorldOpcode.SmsgStandstateChangeFailure, ProtocolGapPackets.Empty()),
        ["player-bind-error"] = (_, _) => (WorldOpcode.SmsgPlayerbinderror, ProtocolGapPackets.Empty()),
        ["clear-far-sight-immediate"] = (_, _) => (WorldOpcode.SmsgClearFarSightImmediate, ProtocolGapPackets.Empty()),
        ["spell-steal-log"] = (_, self) => (WorldOpcode.SmsgSpellsteallog, ProtocolGapPackets.SpellStealLog(self, self, [1459])),
        ["rwhois"] = (_, _) => (WorldOpcode.SmsgRwhois, ProtocolGapPackets.RWhois([new ProtocolGapPackets.RWhoisEntry("wiresample", 0, ["line"])])),
        ["group-cancel"] = (_, _) => (WorldOpcode.SmsgGroupCancel, ProtocolGapPackets.GroupCancel()),
        ["game-time-set"] = (session, _) => (WorldOpcode.SmsgGametimeSet, ProtocolGapPackets.GameTimeSet(GameTimePacker.Pack(WorldStateHooks.For(session.World).LocalNow()))),
        ["game-speed-set"] = (_, _) => (WorldOpcode.SmsgGamespeedSet, ProtocolGapPackets.GameSpeedSet(GameTimePacker.GameSpeedMinutesPerSecond)),
        ["item-query-multiple"] = (_, _) => (WorldOpcode.SmsgItemQueryMultipleResponse, ProtocolGapPackets.ItemQueryMultipleNotFound([6948])),
        ["invalid-promotion-code"] = (_, _) => (WorldOpcode.SmsgInvalidPromotionCode, ProtocolGapPackets.Empty()),
        ["minigame-setup"] = (_, self) => (WorldOpcode.SmsgMinigameSetup, ProtocolGapPackets.MinigameSetup(self, 1, 1, self, 0, [], 0)),
        ["minigame-state"] = (_, _) => (WorldOpcode.SmsgMinigameState, ProtocolGapPackets.MinigameState([], 0)),
        ["npc-wont-talk"] = (_, self) => (WorldOpcode.SmsgNpcWontTalk, ProtocolGapPackets.NpcWontTalk(self)),
        ["ghostee-gone"] = (_, _) => (WorldOpcode.SmsgGhosteeGone, ProtocolGapPackets.Empty()),
        ["chat-player-ambiguous"] = (_, _) => (WorldOpcode.SmsgChatPlayerAmbiguous, ProtocolGapPackets.ChatPlayerAmbiguous("wiresample")),
        ["script-message"] = (_, _) => (WorldOpcode.SmsgScriptMessage, ProtocolGapPackets.Empty()),
        ["gametimebias-set"] = (_, _) => (WorldOpcode.SmsgGametimebiasSet, ProtocolGapPackets.Empty()),
        ["character-profile"] = (_, _) => (WorldOpcode.SmsgCharacterProfile, ProtocolGapPackets.Empty()),
        ["character-profile-realm-connected"] = (_, _) => (WorldOpcode.SmsgCharacterProfileRealmConnected, ProtocolGapPackets.Empty()),
        ["minigame-move-failed"] = (_, _) => (WorldOpcode.SmsgMinigameMoveFailed, ProtocolGapPackets.Empty()),
    };

    private static readonly string Names = string.Join(", ", Samples.Keys);

    /// <summary>The sample names, in the order the help lists them (tests iterate them).</summary>
    internal static IReadOnlyCollection<string> SampleNames => Samples.Keys;

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("wiresample", AccountSecurity.Administrator,
            "Syntax: .wiresample $name\nSend yourself one sample of a server packet no gameplay flow sends, to watch a real client's reaction. Names: " + Names + ".", WireSample),
    ];

    private static bool WireSample(CommandContext context, string text)
    {
        string? name = new CommandArgs(text).ExtractArg();
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        ulong guid = context.Player.Guid.Value; // throws ConsoleInvokerException for a console invoker, which the executor answers
        if (!Samples.TryGetValue(name.ToLowerInvariant(), out Sample? sample))
        {
            context.Reply("Unknown sample '" + ArcaneCore.Kernel.Logging.LogSafe.Escape(name) + "'. Names: " + Names + ".");
            return true;
        }

        (WorldOpcode opcode, byte[] body) = sample(context.Session, guid);
        context.Session.Send(opcode, body);
        context.Reply("Sent " + WorldOpcodeNames.GetName(opcode) + ".");
        return true;
    }
}

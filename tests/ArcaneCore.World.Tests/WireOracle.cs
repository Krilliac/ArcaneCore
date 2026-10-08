using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using WowWorldMessages.Vanilla;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>Opt-in independent decoder for every outbound WorldTestHost packet.</summary>
internal sealed class WireOracle : IOutboundPacketObserver
{
    private static readonly ConcurrentDictionary<WorldOpcode, long> Histogram = new();
    private static readonly ConcurrentDictionary<WorldOpcode, string> GlobalFailures = new();
    private static readonly object DiagnosticFileLock = new();
    // wowm b6839f7 has no server-reader arm or SMSG model for these 1.12.1 opcodes.
    // Each is present in vmangos Server/Protocol/Opcodes_1_12_1.h; body provenance follows.
    private static readonly IReadOnlyDictionary<WorldOpcode, string> ReaderGaps = new Dictionary<WorldOpcode, string>
    {
        // vmangos Server/Packets/Guild.cpp:GuildDeclineNotification::AppendBodyTo.
        [WorldOpcode.SmsgGuildDecline] = "wowm omits the guild decline notification CString",
        // vmangos Chat/Chat.cpp:ChatHandler::BuildChatPacket (variable layout by ChatMsg).
        [WorldOpcode.SmsgMessagechat] = "wowm omits SMSG_MESSAGECHAT's chat-type-dependent body",
        // vmangos Server/Packets/Item.cpp:InventoryChangeFailure::AppendBodyTo.
        [WorldOpcode.SmsgInventoryChangeFailure] = "wowm omits the inventory error variant body",
        // vmangos Server/Packets/Misc.cpp:UpdateAccountDataResponse::AppendBodyTo.
        [WorldOpcode.SmsgUpdateAccountData] = "wowm omits compressed account-data response",
        // vmangos Server/Packets/Battleground.cpp:BattlefieldWin/Lose::AppendBodyTo (empty bodies).
        [WorldOpcode.SmsgBattlefieldWin] = "wowm omits the empty battleground win packet",
        [WorldOpcode.SmsgBattlefieldLose] = "wowm omits the empty battleground loss packet",
        // vmangos Server/Packets/Battleground.cpp:BattlefieldStatus/Empty::AppendBodyTo.
        [WorldOpcode.SmsgBattlefieldStatus] = "wowm omits the active and cleared battleground status variants",
        // wowm reads an unpacked u64 GUID; vmangos Movement/MovementPacketSender.cpp:
        // SendMovementFlagChangeToController writes packed GUID + u32 counter for 1.12.1.
        [WorldOpcode.SmsgForceMoveRoot] = "wowm uses unpacked GUID for 1.12.1 force-root order",
        [WorldOpcode.SmsgForceMoveUnroot] = "wowm uses unpacked GUID for 1.12.1 force-unroot order",
        // vmangos Movement/MovementPacketSender.cpp:SendMovementFlagChangeToAll writes packed GUID.
        [WorldOpcode.SmsgSplineMoveRoot] = "wowm uses unpacked GUID for 1.12.1 spline-root order",
        // wowm uses u16 faction IDs (6 bytes per standing). vmangos Server/Packets/Misc.cpp:
        // SetFactionVisible/SetFactionStanding::AppendBodyTo use u32 reputation list IDs.
        [WorldOpcode.SmsgSetFactionVisible] = "wowm uses u16, but 1.12.1 sends a u32 reputation list ID",
        [WorldOpcode.SmsgSetFactionStanding] = "wowm uses six-byte entries, but 1.12.1 sends u32 ID + i32 standing",
        // vmangos Server/Packets/Misc.cpp:TransferAborted::AppendBodyTo writes only u8 reason.
        [WorldOpcode.SmsgTransferAborted] = "wowm adds map ID and argument to the one-byte 1.12.1 abort reason",
        // vmangos Server/Packets/Spell.cpp:CastResult::AppendBodyTo sends the reason on status 2 (failure).
        [WorldOpcode.SmsgCastResult] = "wowm reads a failure reason on status 0 (success), reversing the 1.12.1 branch",
        // vmangos Server/Packets/Social.cpp:FriendStatus::AppendBodyTo has result-specific online fields.
        [WorldOpcode.SmsgFriendStatus] = "wowm omits the online status, area, level and class suffix",
        // vmangos Server/Packets/Group.cpp:GroupList::AppendBodyTo ends with u8 difficulty.
        [WorldOpcode.SmsgGroupList] = "wowm omits the trailing 1.12.1 group difficulty byte",
        // vmangos Handlers/GroupHandler.cpp:BuildPartyMemberStatsPacket writes u16 zone and negative aura fields.
        [WorldOpcode.SmsgPartyMemberStats] = "wowm reads a u32 zone and omits the server's negative aura layout",
        // vmangos Server/Packets/Guild.cpp:GuildEvent::AppendBodyTo appends the affected GUID where present.
        [WorldOpcode.SmsgGuildEvent] = "wowm omits the event-specific affected GUID",
        // vmangos Chat/Channel.cpp:Make* channel notifications add type-specific GUIDs, names and flags.
        [WorldOpcode.SmsgChannelNotify] = "wowm only models notice type and channel name",
        // vmangos Movement/MovementPacketSender.cpp sends these movement relays to observers;
        // wowm's vanilla ServerOpcodeReader has no server arms for them.
        [WorldOpcode.MsgMoveTeleport] = "wowm lacks the server movement teleport relay",
        [WorldOpcode.MsgMoveSetRunSpeed] = "wowm lacks the server run-speed relay",
        [WorldOpcode.MsgMoveSetRunBackSpeed] = "wowm lacks the server backward-run-speed relay",
        [WorldOpcode.MsgMoveSetWalkSpeed] = "wowm lacks the server walk-speed relay",
        [WorldOpcode.MsgMoveSetSwimSpeed] = "wowm lacks the server swim-speed relay",
        [WorldOpcode.MsgMoveSetSwimBackSpeed] = "wowm lacks the server backward-swim-speed relay",
        [WorldOpcode.MsgMoveSetTurnRate] = "wowm lacks the server turn-rate relay",
        [WorldOpcode.MsgMoveRoot] = "wowm lacks the server movement root relay",
        [WorldOpcode.MsgMoveUnroot] = "wowm lacks the server movement unroot relay",
        [WorldOpcode.MsgMoveKnockBack] = "wowm lacks the server knock-back relay",
        [WorldOpcode.MsgMoveHover] = "wowm lacks the server hover relay",
        [WorldOpcode.MsgMoveWaterWalk] = "wowm lacks the server water-walk relay",
        // vmangos Handlers/GuildHandler.cpp:HandleTabardVendorActivateOpcode sends the vendor GUID.
        [WorldOpcode.MsgTabardvendorActivate] = "wowm lacks the server tabard-vendor activation response",
        // vmangos Server/Packets/Quest.cpp:QuestQueryResponse::AppendBodyTo writes both reputation faction IDs as u32.
        [WorldOpcode.SmsgQuestQueryResponse] = "wowm reads two u16 faction IDs, shifting the 1.12.1 quest response",
        // vmangos Server/Packets/Query.cpp:GameObjectQueryResponse::AppendBodyTo writes 24 raw u32s on 1.12.1.
        [WorldOpcode.SmsgGameobjectQueryResponse] = "wowm reads six raw fields; build 5875 sends 24",
        // vmangos Server/Packets/Spell.cpp:ResurrectRequest::AppendBodyTo sends sickness and delayed bytes.
        [WorldOpcode.SmsgResurrectRequest] = "wowm omits the second resurrect-request boolean",
        // vmangos Server/Packets/Misc.cpp:LogXpGain::AppendBodyTo adds base XP and bonus when xpType is zero.
        [WorldOpcode.SmsgLogXpgain] = "wowm reads the kill-XP suffix for non-kill XP instead",
        // vmangos Objects/Unit.cpp:Unit::SendPeriodicAuraLog writes the damage school as u32.
        [WorldOpcode.SmsgPeriodicauralog] = "wowm reads the periodic-damage school as u8",
        // vmangos Server/Packets/Loot.cpp:operator<<(LootSlotItem) writes 22 bytes per item.
        [WorldOpcode.SmsgLootResponse] = "wowm reads only six bytes per 1.12.1 loot item",
    };
    private readonly ConcurrentDictionary<WorldOpcode, string> _failures = new();
    private readonly ConcurrentDictionary<WorldOpcode, int> _observed = new();
    private readonly bool _recordHistogram;

    public WireOracle(bool recordHistogram = false) => _recordHistogram = recordHistogram;

    static WireOracle()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            string? path = Environment.GetEnvironmentVariable("ARCANECORE_TEST_WIRE_ORACLE_HISTOGRAM");
            if (string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            WriteDiagnostics(path);
        };
    }

    public void Observe(WorldOpcode opcode, ReadOnlyMemory<byte> payload)
    {
        _observed.AddOrUpdate(opcode, 1, (_, count) => count + 1);
        if (_recordHistogram)
            Histogram.AddOrUpdate(opcode, 1, (_, count) => count + 1);
        try
        {
            // vmangos Movement/spline/MoveSplineInit.cpp:Launch sends the stop form only
            // through the move-type byte; wowm always tries to read a spline tail.
            if (opcode == WorldOpcode.SmsgMonsterMove && IsMonsterMoveStop(payload.Span)) return;
            // vmangos MovementInfo.h gates a fixed 24-byte transport block on 0x02000000;
            // wowm gates it on 0x00000200. Keep decoding ordinary heartbeats with wowm.
            if (opcode == WorldOpcode.MsgMoveHeartbeat && IsTransportHeartbeat(payload.Span)) return;
        }
        catch (Exception ex)
        {
            RecordFailure(opcode, payload.Span, ex);
            return;
        }
        if (ReaderGaps.ContainsKey(opcode))
        {
            try { ValidateReaderGap(opcode, payload.Span); }
            catch (Exception ex)
            {
                RecordFailure(opcode, payload.Span, ex);
            }
            return;
        }
        MemoryStream? frame = null;
        try
        {
            // Vanilla SMSG header: BE size including the LE u16 opcode, then the payload.
            byte[] frameBytes = new byte[4 + payload.Length];
            Span<byte> bytes = frameBytes;
            BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)(payload.Length + 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], (ushort)opcode);
            payload.Span.CopyTo(bytes[4..]);
            frame = new MemoryStream(frameBytes, writable: false);
            _ = ServerOpcodeReader.ReadUnencryptedAsync(frame).GetAwaiter().GetResult();
            if (frame.Position != frame.Length)
                throw new InvalidDataException($"{frame.Length - frame.Position} unread trailing bytes");
        }
        catch (Exception ex)
        {
            if (opcode == WorldOpcode.SmsgUpdateObject)
            {
                try
                {
                    if (VmangosTransportUpdateValidator.Validate(payload.Span)) return;
                }
                catch (Exception validationError)
                {
                    RecordFailure(opcode, payload.Span, validationError);
                    return;
                }
            }
            if (opcode is WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo)
            {
                try
                {
                    if (VmangosCombinedSpellTargetValidator.Validate(opcode, payload.Span)) return;
                }
                catch (Exception validationError)
                {
                    RecordFailure(opcode, payload.Span, validationError);
                    return;
                }
            }
            RecordFailure(opcode, payload.Span, ex, frame is null ? null : frame.Position - 4);
        }
        finally { frame?.Dispose(); }
    }

    /// <summary>How many payloads with this opcode reached the oracle, so a test can prove the hook fired.</summary>
    public int ObservedCount(WorldOpcode opcode) => _observed.GetValueOrDefault(opcode);

    public void AssertValid()
        => Assert.True(_failures.IsEmpty, "Wire oracle failed:\n" + string.Join("\n", _failures.Values.Order()));

    private void RecordFailure(WorldOpcode opcode, ReadOnlySpan<byte> payload, Exception ex, long? decoderOffset = null)
    {
        string message = $"{opcode} (0x{(ushort)opcode:X4}, {payload.Length} bytes): {ex.GetType().Name}: {ex.Message}";
        if (Environment.GetEnvironmentVariable("ARCANECORE_TEST_WIRE_ORACLE_TRACE_BYTES") == "1"
            && opcode is WorldOpcode.SmsgUpdateObject or WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo)
            message += $" decoderOffset={decoderOffset} hex={Convert.ToHexString(payload[..Math.Min(payload.Length, 256)])}";
        _failures.TryAdd(opcode, message);
        if (_recordHistogram && GlobalFailures.TryAdd(opcode, message)
            && Environment.GetEnvironmentVariable("ARCANECORE_TEST_WIRE_ORACLE_HISTOGRAM") is { Length: > 0 } path)
            WriteDiagnostics(path);
    }

    private static void WriteDiagnostics(string path)
    {
        lock (DiagnosticFileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            WriteAtomically(path, Histogram.OrderBy(row => (ushort)row.Key)
                .Select(row => $"{row.Key}\t0x{(ushort)row.Key:X4}\t{row.Value}\t{(ReaderGaps.ContainsKey(row.Key) ? "allowlisted" : row.Key is WorldOpcode.SmsgMonsterMove or WorldOpcode.MsgMoveHeartbeat or WorldOpcode.SmsgUpdateObject or WorldOpcode.SmsgSpellStart or WorldOpcode.SmsgSpellGo ? "mixed" : "decoded")}"));
            WriteAtomically(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "failures.tsv"),
                GlobalFailures.OrderBy(row => (ushort)row.Key).Select(row => row.Value));
        }
    }

    // The test host can be killed while ProcessExit runs; a half-written file would read as
    // "no opcodes" or "no failures", so publish each file only once it is complete.
    private static void WriteAtomically(string path, IEnumerable<string> lines)
    {
        string temp = path + ".tmp";
        File.WriteAllLines(temp, lines);
        File.Move(temp, path, overwrite: true);
    }

    private static void ValidateReaderGap(WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (opcode is WorldOpcode.SmsgBattlefieldWin or WorldOpcode.SmsgBattlefieldLose)
        {
            if (!payload.IsEmpty) throw new InvalidDataException("vmangos battleground result has an empty body");
        }
        else if (opcode == WorldOpcode.SmsgTransferAborted)
        {
            if (payload.Length != 1) throw new InvalidDataException("vmangos transfer-aborted body is one u8 reason");
        }
        else if (opcode == WorldOpcode.SmsgCastResult)
        {
            if (payload.Length < 5) throw new InvalidDataException("missing spell ID or cast status");
            bool valid = payload[4] == 0 && payload.Length == 5
                || payload[4] == 2 && payload.Length is 6 or 10 or 14;
            if (!valid) throw new InvalidDataException("vmangos cast-result status/length mismatch");
        }
        else if (opcode == WorldOpcode.SmsgLogXpgain)
        {
            if (payload.Length < 13) throw new InvalidDataException("missing XP gain prefix");
            int expected = payload[12] == 0 ? 21 : 13;
            if (payload.Length != expected)
                throw new InvalidDataException($"vmangos XP gain type expects {expected} bytes");
        }
        else if (opcode == WorldOpcode.SmsgLootResponse)
        {
            if (payload.Length < 10) throw new InvalidDataException("missing loot response prefix");
            int expected = payload[8] == 0 ? 10 : payload.Length >= 14 ? 14 + (22 * payload[13]) : -1;
            if (payload.Length != expected)
                throw new InvalidDataException($"vmangos loot response expects {expected} bytes");
        }
        else if (opcode is WorldOpcode.SmsgForceMoveRoot or WorldOpcode.SmsgForceMoveUnroot or WorldOpcode.SmsgSplineMoveRoot)
        {
            if (payload.IsEmpty) throw new InvalidDataException("missing packed GUID mask");
            int expected = 1 + BitOperations.PopCount((uint)payload[0]);
            if (opcode is WorldOpcode.SmsgForceMoveRoot or WorldOpcode.SmsgForceMoveUnroot) expected += 4;
            if (payload.Length != expected)
                throw new InvalidDataException($"vmangos packed GUID layout expects {expected} bytes");
        }
        else if (opcode == WorldOpcode.SmsgSetFactionVisible)
        {
            if (payload.Length != 4) throw new InvalidDataException("vmangos faction-visible body is one u32 list ID");
        }
        else if (opcode == WorldOpcode.SmsgSetFactionStanding)
        {
            if (payload.Length < 4) throw new InvalidDataException("missing faction standing count");
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            if (payload.Length != 4L + (8L * count))
                throw new InvalidDataException("vmangos faction standing entries are u32 ID + i32 standing");
        }
    }

    private static bool IsMonsterMoveStop(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) return false;
        int moveTypeAt = 1 + BitOperations.PopCount((uint)payload[0]) + 12 + 4;
        if (payload.Length <= moveTypeAt || payload[moveTypeAt] != 1) return false;
        if (payload.Length != moveTypeAt + 1)
            throw new InvalidDataException("vmangos monster-move stop ends after move type");
        return true;
    }

    private static bool IsTransportHeartbeat(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) return false;
        int flagsAt = 1 + BitOperations.PopCount((uint)payload[0]);
        if (payload.Length < flagsAt + 4) return false;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload[flagsAt..]);
        if ((flags & 0x02000000) == 0) return false;
        int expected = flagsAt + 28 + 24;
        if ((flags & 0x00200000) != 0) expected += 4; // swimming pitch
        if ((flags & 0x00002000) != 0) expected += 16; // jump vector
        if ((flags & 0x04000000) != 0) expected += 4; // spline elevation
        if (payload.Length != expected)
            throw new InvalidDataException($"vmangos transport heartbeat expects {expected} bytes");
        return true;
    }
}

using System.Globalization;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// Client requests the 1.12.1 Wow.exe has send code for (static check, no live run) but that no reference server models: vmangos registers each as
/// <c>INVALID_PACKET Unhandled</c> and mangos-classic as <c>Handle_NULL</c>, so there is no retail behaviour to re-express. They are accepted in the
/// world, read through the non-throwing <see cref="PacketReader"/> surface (a short or odd packet is summarised, never thrown at, never a kick), counted
/// per opcode (<see cref="ClientRequestTracker"/>) and reported once per packet as an Info <c>client/unmodelled-request</c> ledger entry, so an
/// operator sees which of them real players use. They change no game state and answer nothing. The layouts known are from the client check
/// (docs/areas/protocol-decisions.md); a field no reference or check names is not guessed: only the length is recorded.
/// <list type="bullet">
/// <item>CMSG_AUTOSTORE_GROUND_ITEM 0x0107 (client 0x5D9D96): vmangos Opcodes.cpp:344, mangos-classic Opcodes.cpp:291.</item>
/// <item>CMSG_STORE_LOOT_IN_SLOT 0x0109 (client 0x5E1B4C): vmangos Opcodes.cpp:346, mangos-classic Opcodes.cpp:293.</item>
/// <item>CMSG_SKILL_BUY_STEP 0x021F (client 0x4D3FBC) and CMSG_SKILL_BUY_RANK 0x0220 (client 0x4D3D58): vmangos Opcodes.cpp:635-636, mangos-classic Opcodes.cpp:571-572.</item>
/// <item>CMSG_SET_LOOKING_FOR_GROUP 0x0200 (client sub_4E88C0): three u32 then a string; vmangos Opcodes.cpp:603.</item>
/// <item>CMSG_CHAT_FILTERED 0x0331 (client 0x49AB50): u64 guid; vmangos Opcodes.cpp:932, mangos-classic Opcodes.cpp:845.</item>
/// <item>CMSG_MINIGAME_MOVE 0x02F8 (client sub_728650): u64 guid, a small field, u32; vmangos Opcodes.cpp:867, mangos-classic Opcodes.cpp:788.</item>
/// </list>
/// Session task; no world-thread state is touched.
/// </summary>
public sealed class ClientRequestHandlers : IOpcodeHandlerGroup
{
    private const int MaxLfgTextBytes = 64;

    public void Register(OpcodeTable table)
    {
        table.OnSession(WorldOpcode.CmsgAutostoreGroundItem, SessionStates.InWorld, static (session, payload) => Observe(WorldOpcode.CmsgAutostoreGroundItem, session, payload));
        table.OnSession(WorldOpcode.CmsgStoreLootInSlot, SessionStates.InWorld, static (session, payload) => Observe(WorldOpcode.CmsgStoreLootInSlot, session, payload));
        table.OnSession(WorldOpcode.CmsgSkillBuyStep, SessionStates.InWorld, static (session, payload) => Observe(WorldOpcode.CmsgSkillBuyStep, session, payload));
        table.OnSession(WorldOpcode.CmsgSkillBuyRank, SessionStates.InWorld, static (session, payload) => Observe(WorldOpcode.CmsgSkillBuyRank, session, payload));
        table.OnSession(WorldOpcode.CmsgSetLookingForGroup, SessionStates.InWorld, static (session, payload) => Observe(WorldOpcode.CmsgSetLookingForGroup, session, payload));
        table.OnSession(WorldOpcode.CmsgChatFiltered, SessionStates.InWorld, static (session, payload) => Observe(WorldOpcode.CmsgChatFiltered, session, payload));
        table.OnSession(WorldOpcode.CmsgMinigameMove, SessionStates.InWorld, static (session, payload) => Observe(WorldOpcode.CmsgMinigameMove, session, payload));
    }

    private static Task Observe(WorldOpcode opcode, WorldSession session, byte[] payload)
    {
        string summary = Summarise(opcode, payload);
        (session.Services.GetService<ClientRequestTracker>() ?? ClientRequestTracker.Shared).Record(opcode, summary);
        AnomalyLedger.Shared.Report("client", "unmodelled-request", WorldOpcodeNames.GetName(opcode), summary, AnomalySeverity.Info);
        return Task.CompletedTask;
    }

    /// <summary>The decoded summary of one request (never throws; the text is escaped and short).</summary>
    internal static string Summarise(WorldOpcode opcode, byte[] payload)
    {
        var reader = new PacketReader(payload);
        string fields = string.Empty;
        switch (opcode)
        {
            case WorldOpcode.CmsgSetLookingForGroup:
                if (reader.TryReadUInt32(out uint first) && reader.TryReadUInt32(out uint second) && reader.TryReadUInt32(out uint third))
                {
                    fields = reader.TryReadCString(MaxLfgTextBytes, out string text)
                        ? string.Create(CultureInfo.InvariantCulture, $" values {first}/{second}/{third} text '{LogSafe.Escape(text, MaxLfgTextBytes)}'")
                        : string.Create(CultureInfo.InvariantCulture, $" values {first}/{second}/{third} text unreadable");
                }

                break;
            case WorldOpcode.CmsgChatFiltered:
            case WorldOpcode.CmsgMinigameMove:
                if (reader.TryReadUInt64(out ulong guid))
                {
                    fields = string.Create(CultureInfo.InvariantCulture, $" guid 0x{guid:X16}");
                }

                break;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{payload.Length} bytes{fields}");
    }
}

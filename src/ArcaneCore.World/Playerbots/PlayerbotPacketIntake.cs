using System.Collections.Frozen;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Party;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The one declared set of server-to-client opcodes a lean bot session (<c>World:Playerbots:ClientFidelity</c> = Lean) keeps in its
/// received-packet ring: exactly the ones some bot code reads back with <c>WorldSession.DrainManagedPackets</c> called with opcodes. Every
/// other packet is dropped before it is copied. A session with an observer attached (scenario tap, outbound observer, packet
/// capture) keeps everything, and so does every session in <see cref="PlayerbotClientFidelity.Full"/>.
/// <para>
/// Adding a reader that drains opcode X means adding <c>X</c> here; <c>PlayerbotPacketIntakeTests</c> scans the
/// source for every such call and fails when an opcode is missing. Immutable; any thread.
/// </para>
/// </summary>
internal static class PlayerbotPacketIntake
{
    /// <summary>The opcodes read back, by reader (economy, loot, instance resets, party, spells, quests, trainers, social).</summary>
    internal static readonly FrozenSet<WorldOpcode> RingOpcodes = new HashSet<WorldOpcode>
    {
        // PlayerbotEconomy: mail and auction replies.
        WorldOpcode.MsgQueryNextMailTime,
        WorldOpcode.SmsgMailListResult,
        WorldOpcode.SmsgSendMailResult,
        WorldOpcode.SmsgAuctionOwnerListResult,
        WorldOpcode.SmsgAuctionListResult,
        WorldOpcode.SmsgAuctionCommandResult,
        // PlayerbotBrain.LootManagedPackets, PlayerbotGroupAI.
        WorldOpcode.SmsgLootResponse,
        // PlayerbotGroupCoordinator.Runs: dungeon reset replies.
        WorldOpcode.SmsgInstanceReset,
        WorldOpcode.SmsgInstanceResetFailed,
        // PlayerbotCombatSpells: cast acceptance and spellbook changes.
        WorldOpcode.SmsgSpellStart,
        WorldOpcode.SmsgSpellGo,
        WorldOpcode.SmsgSpellFailure,
        WorldOpcode.SmsgSpellFailedOther,
        WorldOpcode.SmsgCastResult,
        WorldOpcode.SmsgLearnedSpell,
        WorldOpcode.SmsgSupercededSpell,
        WorldOpcode.SmsgRemovedSpell,
        // PlayerbotQuestGoals: quest dialogue.
        WorldOpcode.SmsgQuestgiverQuestDetails,
        WorldOpcode.SmsgQuestgiverRequestItems,
        WorldOpcode.SmsgQuestgiverOfferReward,
        WorldOpcode.SmsgQuestgiverQuestFailed,
        WorldOpcode.SmsgQuestgiverQuestInvalid,
        // PlayerbotTownGoals: trainer lists.
        WorldOpcode.SmsgTrainerList,
        // PlayerbotTaxi: the reply to a flight activation.
        WorldOpcode.SmsgActivatetaxireply,
    }
    .Concat(PlayerbotPartyAI.IntakeOpcodes)
    .Concat(PlayerbotSocialPackets.IntakeOpcodes)
    .ToFrozenSet();

    /// <summary>True when a lean bot session must copy <paramref name="opcode"/> into its ring.</summary>
    internal static bool Contains(WorldOpcode opcode) => RingOpcodes.Contains(opcode);
}

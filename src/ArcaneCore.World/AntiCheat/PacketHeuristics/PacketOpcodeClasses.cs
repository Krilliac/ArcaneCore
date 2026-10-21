using ArcaneCore.Protocol;

namespace ArcaneCore.World.AntiCheat.PacketHeuristics;

/// <summary>
/// An opcode-name pattern of <see cref="PacketHeuristicsOptions.ReplayExclude"/>: an exact reference name (<c>CMSG_PING</c>) or a name with
/// a leading and/or trailing <c>*</c> (<c>MSG_MOVE_*</c>, <c>*QUERY*</c>). Matched against <see cref="WorldOpcodeNames"/>, case-sensitively.
/// </summary>
internal static class OpcodePatterns
{
    /// <summary>The opcodes whose reference name exists (0 to 827), as names; built once.</summary>
    private static readonly string?[] Names = BuildNames();

    internal static bool TryParse(string? pattern, out (string Core, bool Prefix, bool Suffix) parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(pattern) || pattern != pattern.Trim())
        {
            return false;
        }

        bool leading = pattern[0] == '*';
        bool trailing = pattern.Length > 1 && pattern[^1] == '*';
        string core = pattern[(leading ? 1 : 0)..(trailing ? pattern.Length - 1 : pattern.Length)];
        if (core.Length == 0 || core.Contains('*', StringComparison.Ordinal))
        {
            return false;
        }

        parsed = (core, leading, trailing);
        return true;
    }

    internal static bool MatchesAny(string pattern)
    {
        if (!TryParse(pattern, out var parsed))
        {
            return false;
        }

        foreach (string? name in Names)
        {
            if (name is not null && Matches(name, parsed))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Sets <c>mask[opcode]</c> for every named opcode matching one of <paramref name="patterns"/>; a pattern that does not parse is ignored (the options validation reports it).</summary>
    internal static bool[] Mask(IEnumerable<string> patterns)
    {
        var mask = new bool[PacketHeuristicsOptions.OpcodeSpace];
        foreach (string pattern in patterns)
        {
            if (!TryParse(pattern, out var parsed))
            {
                continue;
            }

            for (int i = 0; i < Names.Length; i++)
            {
                if (Names[i] is { } name && Matches(name, parsed))
                {
                    mask[i] = true;
                }
            }
        }

        return mask;
    }

    private static bool Matches(string name, (string Core, bool Prefix, bool Suffix) pattern) => (pattern.Prefix, pattern.Suffix) switch
    {
        (true, true) => name.Contains(pattern.Core, StringComparison.Ordinal),
        (true, false) => name.EndsWith(pattern.Core, StringComparison.Ordinal),
        (false, true) => name.StartsWith(pattern.Core, StringComparison.Ordinal),
        _ => string.Equals(name, pattern.Core, StringComparison.Ordinal),
    };

    private static string?[] BuildNames()
    {
        var names = new string?[PacketHeuristicsOptions.OpcodeSpace];
        for (int i = 0; i < names.Length; i++)
        {
            string name = WorldOpcodeNames.GetName((uint)i);
            names[i] = name.StartsWith("0x", StringComparison.Ordinal) ? null : name;
        }

        return names;
    }
}

/// <summary>Whether an opcode has a reference name, and if so which direction it names.</summary>
internal enum OpcodeNameKind : byte
{
    /// <summary>No name: outside the opcode table.</summary>
    Unnamed = 0,

    /// <summary>A named client-to-server opcode (CMSG_ or MSG_).</summary>
    Client = 1,

    /// <summary>A named server-to-client opcode (SMSG_).</summary>
    Server = 2,
}

/// <summary>The cadence classes of the <c>cadence</c> check: which opcodes share one one-second window.</summary>
internal enum CadenceClass : byte
{
    None = 0,
    Cast = 1,
    Use = 2,
    Query = 3,
    Emote = 4,
    Ping = 5,
}

/// <summary>
/// Which opcodes belong to which cadence class and which are actions that an incapacitated player cannot send. Both lists are grounded in the
/// handlers and in what a 1.12.1 client does (docs/areas/packet-heuristics.md has the reasoning per opcode). Immutable tables, any thread.
/// </summary>
internal static class PacketOpcodeClasses
{
    internal const int ClassCount = 6;

    /// <summary>Why a player cannot act: the bit of <see cref="ActionMask"/>.</summary>
    [Flags]
    internal enum Incapacity : byte
    {
        None = 0,

        /// <summary>Dead or a ghost (not alive).</summary>
        Dead = 1,

        /// <summary>UNIT_FLAG_TAXI_FLIGHT.</summary>
        Taxi = 4,

        /// <summary>Between a teleport order and its acknowledgement.</summary>
        Teleporting = 8,
    }

    /// <summary>
    /// The opcodes that legitimately repeat an identical payload, so the replay check never looks at them (<see cref="PacketHeuristicsOptions.ReplayBuiltInExclusions"/>).
    /// Movement and its acknowledgements repeat while standing still; ping, queries and selection are idempotent polling; the attack toggles,
    /// cancels, stand state, loot release and area triggers are what the 1.12.1 client and the playerbots resend by design.
    /// </summary>
    internal static readonly string[] BuiltInReplayExclusions =
    [
        "MSG_MOVE_*", "CMSG_MOVE_*", "CMSG_FORCE_*", "CMSG_SET_ACTIVE_MOVER", "CMSG_PING", "*QUERY*",
        "CMSG_SET_SELECTION", "CMSG_ATTACKSWING", "CMSG_ATTACKSTOP", "CMSG_CANCEL_*", "CMSG_LOGOUT_CANCEL",
        "CMSG_STANDSTATECHANGE", "CMSG_LOOT_RELEASE", "CMSG_AREATRIGGER", "CMSG_ZONEUPDATE",
        "CMSG_REQUEST_PARTY_MEMBER_STATS", "CMSG_CHAR_ENUM", "CMSG_BATTLEFIELD_*", "*ACCOUNT_DATA*", "CMSG_PLAYED_TIME",
    ];

    private static readonly byte[] Classes = BuildClasses();
    private static readonly byte[] NameKinds = BuildNameKinds();
    private static readonly byte[] Actions = BuildActions();

    internal static CadenceClass ClassOf(WorldOpcode opcode)
    {
        uint code = (uint)opcode;
        return code < PacketHeuristicsOptions.OpcodeSpace ? (CadenceClass)Classes[code] : CadenceClass.None;
    }

    /// <summary>Whether <paramref name="opcode"/> has a reference name and in which direction; one array read, no allocation.</summary>
    internal static OpcodeNameKind NameKindOf(WorldOpcode opcode)
    {
        uint code = (uint)opcode;
        return code < PacketHeuristicsOptions.OpcodeSpace ? (OpcodeNameKind)NameKinds[code] : OpcodeNameKind.Unnamed;
    }

    /// <summary>The incapacities that make <paramref name="opcode"/> an action the player cannot send (None for opcodes that are not in any list).</summary>
    internal static Incapacity ActionMask(WorldOpcode opcode)
    {
        uint code = (uint)opcode;
        return code < PacketHeuristicsOptions.OpcodeSpace ? (Incapacity)Actions[code] : Incapacity.None;
    }

    private static byte[] BuildNameKinds()
    {
        var kinds = new byte[PacketHeuristicsOptions.OpcodeSpace];
        for (uint i = 0; i < kinds.Length; i++)
        {
            string name = WorldOpcodeNames.GetName(i);
            kinds[i] = (byte)(name.StartsWith("0x", StringComparison.Ordinal)
                ? OpcodeNameKind.Unnamed
                : name.StartsWith("SMSG_", StringComparison.Ordinal) ? OpcodeNameKind.Server : OpcodeNameKind.Client);
        }

        return kinds;
    }

    private static byte[] BuildClasses()
    {
        var classes = new byte[PacketHeuristicsOptions.OpcodeSpace];
        for (uint i = 0; i < classes.Length; i++)
        {
            string name = WorldOpcodeNames.GetName(i);
            if (name.StartsWith("SMSG_", StringComparison.Ordinal))
            {
                continue;
            }

            if (name.Contains("QUERY", StringComparison.Ordinal))
            {
                classes[i] = (byte)CadenceClass.Query;
            }
        }

        Set(classes, CadenceClass.Cast, WorldOpcode.CmsgCastSpell, WorldOpcode.CmsgPetCastSpell);
        Set(classes, CadenceClass.Use, WorldOpcode.CmsgUseItem, WorldOpcode.CmsgGameobjUse, WorldOpcode.CmsgLoot);
        Set(classes, CadenceClass.Emote, WorldOpcode.CmsgEmote, WorldOpcode.CmsgTextEmote);
        Set(classes, CadenceClass.Ping, WorldOpcode.CmsgPing);
        return classes;
    }

    private static void Set(byte[] classes, CadenceClass kind, params WorldOpcode[] opcodes)
    {
        foreach (WorldOpcode opcode in opcodes)
        {
            classes[(ushort)opcode] = (byte)kind;
        }
    }

    private static byte[] BuildActions()
    {
        var actions = new byte[PacketHeuristicsOptions.OpcodeSpace];

        // Dead or ghost. A ghost may release, reclaim, talk to a spirit healer (CMSG_GOSSIP_HELLO is how the client opens one), chat, move,
        // rearrange its bags (PlayerInventory.Operations: "the dead may only rearrange their bags") and cast Reincarnation, so those are
        // absent: swap and split, gossip, spirit healer, cast. What is listed is refused by the handlers (ItemHandlers: use needs
        // IsAlive; CombatHandlers: attack needs IsAlive; PlayerInventory: CanUseItem, equip) or has no meaning without a body.
        Add(actions, Incapacity.Dead,
            WorldOpcode.CmsgUseItem, WorldOpcode.CmsgLoot, WorldOpcode.CmsgLootMoney, WorldOpcode.CmsgAutostoreLootItem,
            WorldOpcode.CmsgInitiateTrade, WorldOpcode.CmsgAutoequipItem, WorldOpcode.CmsgAutoequipItemSlot,
            WorldOpcode.CmsgAttackswing, WorldOpcode.CmsgBuyItem, WorldOpcode.CmsgSellItem);

        // Stunned is not listed: the server itself refuses a cast or item use for a stun only when the caster is not immune to the stun mechanic
        // and the spell is instant or has the stun interrupt flag (CasterAuraGate.Check, vmangos Spell::CheckCasterAuras), and UNIT_FLAG_STUNNED is
        // also set during the logout countdown (CcState.RefreshStun). A packet-level check cannot know the spell's cast time or immunity cheaply, so
        // reporting would flag legitimate casts; the cast and item-use cases were the only stun entries, so the stun case is dropped.

        // On a taxi the client disables every action bar button and every NPC and object interaction.
        Add(actions, Incapacity.Taxi,
            WorldOpcode.CmsgCastSpell, WorldOpcode.CmsgUseItem, WorldOpcode.CmsgGameobjUse, WorldOpcode.CmsgLoot, WorldOpcode.CmsgLootMoney,
            WorldOpcode.CmsgAutostoreLootItem, WorldOpcode.CmsgInitiateTrade, WorldOpcode.CmsgAttackswing, WorldOpcode.CmsgGossipHello,
            WorldOpcode.CmsgQuestgiverHello, WorldOpcode.CmsgBuyItem, WorldOpcode.CmsgSellItem, WorldOpcode.CmsgAutoequipItem);

        // Teleporting: between the order and the ack the client shows a loading screen (far) or is repositioned (near). Packets already in
        // flight can still arrive, so this list is only the actions with a world effect.
        Add(actions, Incapacity.Teleporting,
            WorldOpcode.CmsgCastSpell, WorldOpcode.CmsgUseItem, WorldOpcode.CmsgGameobjUse, WorldOpcode.CmsgLoot, WorldOpcode.CmsgInitiateTrade);
        return actions;
    }

    private static void Add(byte[] actions, Incapacity why, params WorldOpcode[] opcodes)
    {
        foreach (WorldOpcode opcode in opcodes)
        {
            actions[(ushort)opcode] |= (byte)why;
        }
    }
}

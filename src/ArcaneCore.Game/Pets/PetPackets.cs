using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Pets;

/// <summary>vmangos PetTameFailureReason (SharedDefines.h:1707-1721).</summary>
public enum PetTameFailureReason : byte
{
    InvalidCreature = 1,
    TooMany = 2,
    CreatureAlreadyOwned = 3,
    NotTameable = 4,
    AnotherSummonActive = 5,
    UnitsCantTame = 6,
    NoPetAvailable = 7,
    Dead = 10,
    NotDead = 11, // vmangos SharedDefines.h:1720, PETTAME_NOTDEAD.
    TooHighLevel = 9,
}

/// <summary>SMSG_PET_ACTION_FEEDBACK message (wow_messages PetFeedback; vmangos FEEDBACK_*).</summary>
public enum PetFeedback : byte
{
    PetDead = 1,
    NothingToAttack = 2,
    CantAttackTarget = 3,
    NoPathTo = 4,
}

/// <summary>SMSG_PET_ACTION_SOUND reason (wow_messages PetTalkReason; vmangos PET_TALK_*).</summary>
public enum PetTalk : uint
{
    SpecialSpell = 0,
    Attack = 1,
}

/// <summary>CMSG_PET_ACTION (wow_messages cmsg_pet_action.wowm): u64 pet, u32 data, u64 target.</summary>
public readonly record struct PetActionRequest(ObjectGuid Pet, uint Data, ObjectGuid Target)
{
    /// <summary>vmangos UNIT_ACTION_BUTTON_ACTION.</summary>
    public uint Action => Data & 0x00FFFFFF;

    /// <summary>vmangos UNIT_ACTION_BUTTON_TYPE.</summary>
    public byte Type => (byte)(Data >> 24);
}

/// <summary>CMSG_PET_SET_ACTION (cmsg_pet_set_action.wowm): a pet guid and one or two (position, data) pairs.</summary>
public sealed record PetSetActionRequest(ObjectGuid Pet, IReadOnlyList<(uint Position, uint Data)> Actions);

/// <summary>CMSG_PET_SPELL_AUTOCAST (cmsg_pet_spell_autocast.wowm): u64 pet, u32 spell, u8 state.</summary>
public readonly record struct PetAutocastRequest(ObjectGuid Pet, uint Spell, bool Enabled);

public readonly record struct PetRenameRequest(ObjectGuid Pet, string Name);

/// <summary>CMSG_PET_CAST_SPELL for 1.12 (cmsg_pet_cast_spell.wowm): u64 pet, u32 spell, SpellCastTargets.</summary>
public sealed record PetCastRequest(ObjectGuid Pet, uint Spell, SpellCastTargets Targets);

/// <summary>
/// The pet packets of build 5875 (docs/integration/pets.md): layouts from gtker/wow_messages
/// <c>world/pet/*.wowm</c> and <c>queries/*pet_name*.wowm</c>, checked against vmangos
/// (<c>Player::PetSpellInitialize</c>, <c>Server/Packets/Pet.cpp</c>) and mangos-classic.
/// </summary>
public static class PetPackets
{
    /// <summary>SMSG_PET_TAME_FAILURE: one-byte PetTameFailureReason (vmangos Player::SendPetTameFailure).</summary>
    public static byte[] BuildTameFailure(PetTameFailureReason reason) => [(byte)reason];
    // --- client packets -----------------------------------------------------------------------------

    public static PetActionRequest ReadAction(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        return new PetActionRequest(new ObjectGuid(reader.ReadUInt64()), reader.ReadUInt32(), new ObjectGuid(reader.ReadUInt64()));
    }

    /// <summary>
    /// vmangos PetSetAction::ReadFromWorldPacket (Packets/Pet.cpp:52-62): a packet of exactly 24 bytes
    /// (8 + 2 * 8) carries two actions, anything else one.
    /// </summary>
    public static PetSetActionRequest ReadSetAction(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        var pet = new ObjectGuid(reader.ReadUInt64());
        int count = payload.Length == 24 ? 2 : 1;
        var actions = new List<(uint, uint)>(count);
        for (int i = 0; i < count; i++)
        {
            actions.Add((reader.ReadUInt32(), reader.ReadUInt32()));
        }

        return new PetSetActionRequest(pet, actions);
    }

    public static PetAutocastRequest ReadAutocast(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        return new PetAutocastRequest(new ObjectGuid(reader.ReadUInt64()), reader.ReadUInt32(), reader.ReadByte() != 0);
    }

    public static ObjectGuid ReadGuid(ReadOnlySpan<byte> payload) => new(new PacketReader(payload).ReadUInt64());

    /// <summary>CMSG_PET_CANCEL_AURA: u64 pet, u32 spell.</summary>
    public static (ObjectGuid Pet, uint Spell) ReadCancelAura(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        return (new ObjectGuid(reader.ReadUInt64()), reader.ReadUInt32());
    }

    public static PetCastRequest ReadCast(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        var pet = new ObjectGuid(reader.ReadUInt64());
        uint spell = reader.ReadUInt32();
        return new PetCastRequest(pet, spell, SpellCastTargets.Read(ref reader));
    }

    /// <summary>CMSG_PET_NAME_QUERY: u32 pet number, u64 pet guid.</summary>
    public static (uint PetNumber, ObjectGuid Pet) ReadNameQuery(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        return (reader.ReadUInt32(), new ObjectGuid(reader.ReadUInt64()));
    }

    /// <summary>CMSG_PET_RENAME: u64 pet guid followed by a NUL-terminated name.</summary>
    public static PetRenameRequest ReadRename(ReadOnlySpan<byte> payload)
    {
        var reader = new PacketReader(payload);
        return new PetRenameRequest(new ObjectGuid(reader.ReadUInt64()), reader.ReadCString());
    }

    // --- server packets -----------------------------------------------------------------------------

    /// <summary>
    /// SMSG_PET_SPELLS (vmangos Player::PetSpellInitialize, Player.cpp:17354-17398): the full pet GUID,
    /// u32 duration 0, react, command, a zero byte, the enabled byte (0 enabled, 0x8 disabled, vmangos
    /// <c>IsEnabled() ? 0x0 : 0x8</c>; wow_messages names the byte the other way round), the ten
    /// action words, the spell words (only a permanent pet lists its spells) and the cooldowns.
    /// The cooldown list follows wow_messages and mangos-classic (u8 count, then u16 spell, u16
    /// category, u32 cooldown, u32 category cooldown): vmangos writes a u16 count and a u32 spell id
    /// there, which agrees with them only for an empty list (docs/integration/pets.md).
    /// </summary>
    public static byte[] BuildPetSpells(Creature pet, CharmInfo charm, bool listSpells, IReadOnlyList<InitialSpellCooldown>? cooldowns = null)
    {
        var w = new PacketWriter(96);
        w.WriteUInt64(pet.Guid.Value);
        w.WriteUInt32(0);
        w.WriteByte((byte)charm.ReactState);
        w.WriteByte((byte)charm.CommandState);
        w.WriteByte(0);
        w.WriteByte(charm.Enabled ? (byte)0x0 : (byte)0x8);
        foreach (ActionButton button in charm.ActionBar)
        {
            w.WriteUInt32(button.Packed);
        }

        var spells = new List<uint>();
        if (listSpells)
        {
            foreach ((uint spell, ActionType state) in charm.SpellStates)
            {
                spells.Add(ActionButton.Make(spell, state).Packed);
            }
        }

        w.WriteByte((byte)spells.Count);
        foreach (uint word in spells)
        {
            w.WriteUInt32(word);
        }

        IReadOnlyList<InitialSpellCooldown> running = cooldowns ?? [];
        w.WriteByte((byte)running.Count);
        foreach (InitialSpellCooldown cooldown in running)
        {
            w.WriteUInt16((ushort)cooldown.SpellId);
            w.WriteUInt16((ushort)cooldown.Category);
            w.WriteUInt32(cooldown.CooldownMs);
            w.WriteUInt32(cooldown.CategoryCooldownMs);
        }

        return w.ToArray();
    }

    /// <summary>vmangos Player::RemovePetActionBar (Player.cpp:17507-17511): SMSG_PET_SPELLS with an empty GUID.</summary>
    public static byte[] BuildRemoveActionBar()
    {
        var w = new PacketWriter(8);
        w.WriteUInt64(0);
        return w.ToArray();
    }

    /// <summary>Build-5875 SMSG_PET_NAME_INVALID has an empty body (vmangos Pet.cpp).</summary>
    public static byte[] BuildNameInvalid() => [];

    /// <summary>SMSG_PET_MODE (smsg_pet_mode.wowm): u64 guid, react, command, a zero byte, the enabled byte.</summary>
    public static byte[] BuildPetMode(Creature pet, CharmInfo charm)
    {
        var w = new PacketWriter(12);
        w.WriteUInt64(pet.Guid.Value);
        w.WriteByte((byte)charm.ReactState);
        w.WriteByte((byte)charm.CommandState);
        w.WriteByte(0);
        w.WriteByte(charm.Enabled ? (byte)0x0 : (byte)0x8);
        return w.ToArray();
    }

    /// <summary>SMSG_PET_ACTION_FEEDBACK: u8 message (vmangos Unit::SendPetActionFeedback, Unit.cpp:9041).</summary>
    public static byte[] BuildActionFeedback(PetFeedback feedback) => [(byte)feedback];

    /// <summary>SMSG_PET_CAST_FAILED, 1.12 form (smsg_pet_cast_failed.wowm): u32 spell, u8 status 2 (vmangos SPELL_RESULT_STATUS_FAIL), u8 result.</summary>
    public static byte[] BuildCastFailed(uint spellId, SpellCastResult result)
    {
        var w = new PacketWriter(6);
        w.WriteUInt32(spellId);
        w.WriteByte(2);
        w.WriteByte((byte)result);
        return w.ToArray();
    }

    /// <summary>SMSG_PET_NAME_QUERY_RESPONSE (smsg_pet_name_query_response.wowm): u32 pet number, name, u32 name timestamp.</summary>
    public static byte[] BuildNameQueryResponse(uint petNumber, string name, uint nameTimestamp)
    {
        var w = new PacketWriter(16 + name.Length);
        w.WriteUInt32(petNumber);
        w.WriteCString(name);
        w.WriteUInt32(nameTimestamp);
        return w.ToArray();
    }

    /// <summary>SMSG_PET_ACTION_SOUND: u64 guid, u32 reason.</summary>
    public static byte[] BuildActionSound(ObjectGuid pet, PetTalk talk)
    {
        var w = new PacketWriter(12);
        w.WriteUInt64(pet.Value);
        w.WriteUInt32((uint)talk);
        return w.ToArray();
    }

    /// <summary>SMSG_AI_REACTION: u64 guid, u32 reaction (2 = AI_REACTION_HOSTILE, which plays the aggro sound).</summary>
    public static byte[] BuildAiReaction(ObjectGuid unit, uint reaction = 2)
    {
        var w = new PacketWriter(12);
        w.WriteUInt64(unit.Value);
        w.WriteUInt32(reaction);
        return w.ToArray();
    }
}

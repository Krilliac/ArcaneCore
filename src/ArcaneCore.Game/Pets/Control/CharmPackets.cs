using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Pets.Control;

/// <summary>
/// The unit-control packets of build 5875: SMSG_CLIENT_CONTROL_UPDATE (vmangos <c>Player::SetClientControl</c>, Player.cpp:20123-20132,
/// <c>ClientControlUpdate::AppendBodyTo</c>, Packets/Misc.cpp:999-1004; wow_messages <c>smsg_client_control_update.wowm</c>: packed GUID,
/// u8 allow movement) and the possess and charm forms of SMSG_PET_SPELLS (vmangos <c>Player::PossessSpellInitialize</c> and
/// <c>Player::CharmSpellInitialize</c>, Player.cpp:17400-17505).
/// </summary>
public static class CharmPackets
{
    /// <summary>SMSG_CLIENT_CONTROL_UPDATE: the packed GUID of the unit and whether the client may move it.</summary>
    public static byte[] BuildClientControlUpdate(ObjectGuid unit, bool allowMove)
    {
        var w = new PacketWriter(10);
        w.WritePackedGuid(unit.Value);
        w.WriteByte(allowMove ? (byte)1 : (byte)0);
        return w.ToArray();
    }

    /// <summary>
    /// vmangos Player::PossessSpellInitialize: the GUID, the possess aura's remaining duration (0 when none of the possessor's has one),
    /// a u32 0 where a pet carries its react and command state, the ten action words, no spells, then the cooldowns (written as
    /// <see cref="PetPackets.BuildPetSpells"/> writes them).
    /// </summary>
    public static byte[] BuildPossessSpells(Unit charm, CharmInfo info, int durationMs, IReadOnlyList<InitialSpellCooldown>? cooldowns = null)
    {
        var w = new PacketWriter(64);
        w.WriteUInt64(charm.Guid.Value);
        w.WriteInt32(durationMs);
        w.WriteUInt32(0);
        WriteBar(w, info);
        w.WriteByte(0);
        WriteCooldowns(w, cooldowns);
        return w.ToArray();
    }

    /// <summary>
    /// vmangos Player::CharmSpellInitialize: the GUID, the charm aura's remaining duration, react and command state, two zero bytes, the
    /// ten action words, then the charm spells (a warlock's charmed demon only: <paramref name="spellWords"/>) and the cooldowns.
    /// </summary>
    public static byte[] BuildCharmSpells(Unit charm, CharmInfo info, int durationMs, IReadOnlyList<uint> spellWords,
        IReadOnlyList<InitialSpellCooldown>? cooldowns = null)
    {
        var w = new PacketWriter(80);
        w.WriteUInt64(charm.Guid.Value);
        w.WriteInt32(durationMs);
        w.WriteByte((byte)info.ReactState);
        w.WriteByte((byte)info.CommandState);
        w.WriteByte(0);
        w.WriteByte(0);
        WriteBar(w, info);
        w.WriteByte((byte)spellWords.Count);
        foreach (uint word in spellWords)
        {
            w.WriteUInt32(word);
        }

        WriteCooldowns(w, cooldowns);
        return w.ToArray();
    }

    private static void WriteBar(PacketWriter w, CharmInfo info)
    {
        foreach (ActionButton button in info.ActionBar)
        {
            w.WriteUInt32(button.Packed);
        }
    }

    private static void WriteCooldowns(PacketWriter w, IReadOnlyList<InitialSpellCooldown>? cooldowns)
    {
        IReadOnlyList<InitialSpellCooldown> running = cooldowns ?? [];
        w.WriteByte((byte)running.Count);
        foreach (InitialSpellCooldown cooldown in running)
        {
            w.WriteUInt16((ushort)cooldown.SpellId);
            w.WriteUInt16((ushort)cooldown.Category);
            w.WriteUInt32(cooldown.CooldownMs);
            w.WriteUInt32(cooldown.CategoryCooldownMs);
        }
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// Resolves the creature entry of a mount spell (SPELL_AURA_MOUNTED's misc value) to the display id the rider wears (vmangos
/// HandleAuraMounted: <c>GetCreatureTemplate(entry)</c> then <c>Creature::ChooseDisplayId</c> and
/// <c>GetCreatureDisplayInfoRandomGender</c>, SpellAuras.cpp:2251-2276). The creature data lives in the world daemon, which
/// registers the source with <see cref="LocomotionEnvironment.RegisterMountDisplays"/>.
/// </summary>
public interface IMountDisplaySource
{
    /// <summary>The display id for the creature entry, or null when the creature template does not exist.</summary>
    uint? FindMountDisplay(uint creatureEntry);
}

/// <summary>UnitMountResult / UnitDismountResult as the client reads them (vmangos UnitDefines.h; gtker smsg_mountresult.wowm, smsg_dismountresult.wowm).</summary>
public static class MountResults
{
    public const uint AlreadyMounted = 2;
    public const uint NotMountable = 3;
    public const uint Looting = 6;
    public const uint Ok = 10;
    public const uint DismountNotMounted = 1;
    public const uint DismountOk = 3;
}

/// <summary>
/// Mounting and dismounting a unit (vmangos Unit::Mount / Unit::Unmount, Unit.cpp:5794-5842, and Player::Mount /
/// Player::Unmount, Player.cpp:18170-18260). The mount state is the mount display field (1.12 has no mounted unit flag).
/// <para>
/// A player is refused with SMSG_MOUNTRESULT when already mounted (2) or looting (6); otherwise the auras that end on mounting
/// are removed, the display is set and SMSG_MOUNTRESULT 10 (ok) is sent. Dismounting removes the auras that end on dismount,
/// clears the display and sends SMSG_DISMOUNTRESULT 3 (ok); a unit that is not mounted is left alone, silently when it
/// comes from an aura. Not delivered because the systems are not on this base: unsummoning or disabling the player's pet
/// (Player::Mount :18205-18218, Player::Unmount :18245-18250), refusing a disallowed shapeshift form (only for mounts without
/// a spell), and ResetExtraAttacks.
/// </para>
/// </summary>
public static class MountService
{
    public static bool IsMounted(Unit unit) => UnitSpeed.IsMounted(unit);

    /// <summary>Returns true when the unit is mounted afterwards by this call.</summary>
    public static bool Mount(SpellSystem system, Unit unit, uint displayId)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit is Player player)
        {
            if (IsMounted(player))
            {
                SendMountResult(player, MountResults.AlreadyMounted);
                return false;
            }

            if ((player.UnitFlags & UnitFlags.Looting) != 0)
            {
                system.RemoveAurasByType(player, AuraType.Mounted);
                SendMountResult(player, MountResults.Looting);
                return false;
            }
        }

        system.InterruptChannelsWithFlags(unit, SpellAuraInterruptFlags.MountCancels);
        system.RemoveAurasWithInterruptFlags(unit, SpellAuraInterruptFlags.MountCancels);
        unit.SetUInt32(UpdateFields.UnitFieldMountdisplayid, displayId);
        if (unit is Creatures.Creature)
        {
            UnitSpeed.UpdateSpeed(unit, MoveType.Walk);
            UnitSpeed.UpdateSpeed(unit, MoveType.Run);
        }

        if (unit is Player mounted)
        {
            SendMountResult(mounted, MountResults.Ok);
        }

        return true;
    }

    public static void Unmount(SpellSystem system, Unit unit)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(unit);
        if (!IsMounted(unit))
        {
            return;
        }

        system.InterruptChannelsWithFlags(unit, SpellAuraInterruptFlags.DismountCancels);
        system.RemoveAurasWithInterruptFlags(unit, SpellAuraInterruptFlags.DismountCancels);
        unit.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0);
        if (unit is Creatures.Creature)
        {
            UnitSpeed.UpdateSpeed(unit, MoveType.Walk);
            UnitSpeed.UpdateSpeed(unit, MoveType.Run);
        }

        if (unit is Player player)
        {
            player.Session.Send(WorldOpcode.SmsgDismountresult, ResultPacket(MountResults.DismountOk));
        }
    }

    private static void SendMountResult(Player player, uint result)
        => player.Session.Send(WorldOpcode.SmsgMountresult, ResultPacket(result));

    /// <summary>SMSG_MOUNTRESULT / SMSG_DISMOUNTRESULT: a u32 result.</summary>
    public static byte[] ResultPacket(uint result)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(result);
        return writer.ToArray();
    }
}

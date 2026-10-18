using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Locomotion;

/// <summary>EnvironmentalDamageType (vmangos Player.h:590-598; the wire value of SMSG_ENVIRONMENTAL_DAMAGE_LOG).</summary>
public enum EnvironmentalDamageType : byte
{
    Exhausted = 0,
    Drowning = 1,
    Fall = 2,
    Lava = 3,
    Slime = 4,
    Fire = 5,

    /// <summary>vmangos' "custom case for fall without durability loss"; logged to the client as <see cref="Fall"/>.</summary>
    FallToVoid = 6,
}

/// <summary>The school a piece of environmental damage is checked against (vmangos Player::EnvironmentalDamage, Player.cpp:719-758).</summary>
public enum EnvironmentalSchool
{
    Physical,
    Fire,
    Nature,
}

/// <summary>What the target's auras do to environmental damage: <paramref name="Absorb"/> and <paramref name="Resist"/> as vmangos CalculateDamageAbsorbAndResist returns them.</summary>
public readonly record struct EnvironmentalMitigation(uint Absorb, int Resist);

/// <summary>
/// Absorb, resist and immunity for environmental damage (vmangos <c>IsImmuneToDamage</c>, <c>CalculateDamageAbsorbAndResist</c>).
/// The spell system's <see cref="Spells.SpellEnvironmentalMitigation"/> answers them from the live auras and the spell combat
/// rules; the world daemon registers it with <see cref="LocomotionEnvironment.RegisterMitigation"/>. With nothing registered
/// <see cref="NoEnvironmentalMitigation"/> mitigates nothing. Falling, drowning and fatigue are never absorbed (client 1.7.0
/// and later, Player.cpp:743-750); only their immunity is asked.
/// </summary>
public interface IEnvironmentalDamageMitigation
{
    /// <summary>Immune to the school: no damage and no packet.</summary>
    bool IsImmune(Player player, EnvironmentalSchool school);

    /// <summary>Absorb and resist for fire (lava, fire) or nature (slime) damage.</summary>
    EnvironmentalMitigation Calculate(Player player, EnvironmentalSchool school, uint damage);
}

/// <summary>The pass-through mitigation of a world without a spell system.</summary>
public sealed class NoEnvironmentalMitigation : IEnvironmentalDamageMitigation
{
    public static NoEnvironmentalMitigation Instance { get; } = new();

    public bool IsImmune(Player player, EnvironmentalSchool school) => false;

    public EnvironmentalMitigation Calculate(Player player, EnvironmentalSchool school, uint damage) => default;
}

/// <summary>
/// Damage a player takes from the world rather than from a unit: falls, drowning, fatigue, lava (vmangos
/// Player::EnvironmentalDamage, Player.cpp:713-775).
/// </summary>
public static class EnvironmentalDamage
{
    /// <summary>SMSG_ENVIRONMENTAL_DAMAGE_LOG: u64 victim GUID, u8 type, u32 damage, u32 absorb, i32 resist (vmangos Combat.cpp:105-127; FALL_TO_VOID is logged as FALL, Unit.cpp:5147).</summary>
    public static byte[] BuildLog(ulong guid, EnvironmentalDamageType type, uint damage, uint absorb, int resist)
    {
        var writer = new PacketWriter(21);
        writer.WriteUInt64(guid);
        writer.WriteByte((byte)(type == EnvironmentalDamageType.FallToVoid ? EnvironmentalDamageType.Fall : type));
        writer.WriteUInt32(damage);
        writer.WriteUInt32(absorb);
        writer.WriteInt32(resist);
        return writer.ToArray();
    }

    /// <summary>
    /// Hurt the player. Nothing happens to a dead player or a game master. Immunity ends it without a packet; fire/lava
    /// and slime are reduced by the mitigation (vmangos subtracts absorb, and resist when it is positive; a negative
    /// resist adds to the damage). A player on a taxi flight, or a priest in the Spirit of Redemption, then takes none of
    /// it and the log shows it absorbed (<see cref="MapCombat.DealDamageMods"/>, Player.cpp:756). The log goes to the player
    /// and the observers, the damage is dealt as self damage (no combat, threat or PvP flag), and a player who dies loses 10%
    /// durability on the worn items, followed by the empty SMSG_DURABILITY_DAMAGE_DEATH ("Confirmed on classic that dying from
    /// lava, fatigue and drowning causes durability loss"). The mitigation is the one registered for <paramref name="world"/>.
    /// Returns the damage dealt.
    /// </summary>
    public static uint Apply(WorldRuntime world, Player player, EnvironmentalDamageType type, uint damage)
    {
        ArgumentNullException.ThrowIfNull(world);
        return Apply(player, type, damage, LocomotionEnvironment.MitigationFor(world));
    }

    /// <summary>
    /// <see cref="Apply(WorldRuntime, Player, EnvironmentalDamageType, uint)"/> with the immunity, absorb and resist of
    /// <paramref name="mitigation"/>: the spell system's SPELL_EFFECT_ENVIRONMENTAL_DAMAGE passes its own.
    /// </summary>
    public static uint Apply(Player player, EnvironmentalDamageType type, uint damage, IEnvironmentalDamageMitigation mitigation)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(mitigation);
        if (!player.IsAlive || player.IsGameMaster || player.Map is not { } map)
        {
            return 0;
        }

        EnvironmentalSchool? school = type switch
        {
            EnvironmentalDamageType.Lava or EnvironmentalDamageType.Fire => EnvironmentalSchool.Fire,
            EnvironmentalDamageType.Slime => EnvironmentalSchool.Nature,
            EnvironmentalDamageType.Exhausted or EnvironmentalDamageType.Drowning or EnvironmentalDamageType.Fall => EnvironmentalSchool.Physical,
            _ => null,
        };

        uint absorb = 0;
        int resist = 0;
        if (school is { } s)
        {
            if (mitigation.IsImmune(player, s))
            {
                return 0;
            }

            if (s != EnvironmentalSchool.Physical)
            {
                EnvironmentalMitigation m = mitigation.Calculate(player, s, damage);
                absorb = m.Absorb;
                resist = m.Resist;
            }
        }

        uint bonus = resist < 0 ? (uint)Math.Abs((long)resist) : 0;
        damage += bonus;
        uint malus = resist > 0 ? absorb + (uint)resist : absorb;
        damage = damage <= malus ? 0 : damage - malus;

        // DealDamageMods(this, damage, &absorb) before the log (Player.cpp:756-758).
        map.Combat.DealDamageMods(player, player, ref damage, ref absorb);

        CombatPackets.SendToSet(player, WorldOpcode.SmsgEnvironmentaldamagelog, BuildLog(player.Guid.Value, type, damage, absorb, resist));

        // Player::EnvironmentalDamage suppresses Unit::Kill wear, then charges it once below.
        uint dealt = map.Combat.DealDamage(player, player, damage, direct: false, meleeDamage: false, durabilityLoss: false);
        if (!player.IsAlive)
        {
            player.LastMissingCasterPeriodicDeath = false; // this death has one source
            player.LastEnvironmentalDeath = type;
            player.Inventory.DurabilityLossAll(0.10, inventory: false);
            player.Session.Send(WorldOpcode.SmsgDurabilityDamageDeath, []);
        }

        return dealt;
    }
}

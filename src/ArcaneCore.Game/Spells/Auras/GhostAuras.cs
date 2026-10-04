using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_AURA_GHOST (95), after vmangos <c>Aura::HandleAuraGhost</c> (SpellAuras.cpp:5639-5659): the visibility flag
/// UNIT_VIS_FLAGS_GHOST (0x01, UNIT_FIELD_BYTES_1 byte 3) and, on a player, PLAYER_FLAGS_GHOST. Removal clears both. The group
/// status (the ghost bit of the member stats) is read from the player flag when the group's stats are diffed, so no group flag is
/// raised here. The ghost spell 8326 also carries the +25% run and swim speed (auras 31 and 58, the speed auras); its water
/// walking is ordered by combat, as in vmangos.
/// </summary>
public sealed class GhostAuras : ISpellHandlerModule
{
    /// <summary>UNIT_BYTES_1_OFFSET_VIS_FLAG: the byte of UNIT_FIELD_BYTES_1 that holds the visibility flags.</summary>
    public const int VisFlagByte = 3;

    /// <summary>UNIT_VIS_FLAGS_GHOST.</summary>
    public const byte VisFlagGhost = 0x01;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.Ghost, new AuraHandler((s, holder, aura, apply) => Apply(holder.Target, apply), null));
    }

    private static void Apply(Unit target, bool apply)
    {
        byte vis = target.GetByte(UpdateFields.UnitFieldBytes1, VisFlagByte);
        target.SetByte(UpdateFields.UnitFieldBytes1, VisFlagByte, apply ? (byte)(vis | VisFlagGhost) : (byte)(vis & ~VisFlagGhost));
        if (target is Player player)
        {
            player.Flags = apply ? player.Flags | PlayerFlags.Ghost : player.Flags & ~PlayerFlags.Ghost;
        }
    }
}

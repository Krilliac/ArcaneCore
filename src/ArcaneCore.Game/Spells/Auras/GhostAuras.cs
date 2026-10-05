using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_AURA_GHOST (95): the unit visibility byte and player ghost flag, following
/// vmangos HandleAuraGhost. Water walking and the combat death state have separate owners.
/// </summary>
public sealed class GhostAuras : ISpellHandlerModule
{
    private const int VisibilityByte = 3;
    private const byte GhostVisibility = 0x01;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.Ghost, new AuraHandler(Apply, null));
    }

    private static void Apply(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        byte flags = target.GetByte(UpdateFields.UnitFieldBytes1, VisibilityByte);
        target.SetByte(UpdateFields.UnitFieldBytes1, VisibilityByte,
            apply ? (byte)(flags | GhostVisibility) : (byte)(flags & ~GhostVisibility));
        if (target is Player player)
        {
            player.Flags = apply ? player.Flags | PlayerFlags.Ghost : player.Flags & ~PlayerFlags.Ghost;
        }
    }
}

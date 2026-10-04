using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Stealth;

/// <summary>Vanilla invisibility state (vmangos SpellAuras.cpp:3711-3779).</summary>
public static class InvisibilityAuras
{
    /// <summary>PLAYER_FIELD_BYTE2_INVISIBILITY_GLOW (vmangos Player.h:390).</summary>
    public const byte GlowFlag = 0x40;

    public static AuraHandler InvisibilityHandler(StealthRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new AuraHandler((spells, holder, _, apply) =>
        {
            Unit target = holder.Target;
            if (apply)
            {
                // vmangos SpellAuras.cpp:3717-3734. The holder is already in the aura list.
                spells.RemoveAurasWithInterruptFlags(target, AuraInterruptMask.StealthInvisibility);
                if (target is Player)
                {
                    target.SetByte(UpdateFields.PlayerFieldBytes2, StealthAuras.PlayerFlagsByte,
                        (byte)(target.GetByte(UpdateFields.PlayerFieldBytes2, StealthAuras.PlayerFlagsByte) | GlowFlag));
                }

                if (registry.VisibilityOf(target) == StealthVisibility.On)
                {
                    registry.SetVisibility(target, StealthVisibility.NoDetect);
                    target.Map?.RefreshVisibility(target);
                    registry.SetVisibility(target, StealthVisibility.Invisibility);
                    target.Map?.RefreshVisibility(target);
                }
            }
            else if (!spells.HasAuraType(target, AuraType.ModInvisibility))
            {
                // vmangos SpellAuras.cpp:3739-3759: only the last invisibility aura clears glow and restores visibility.
                if (target is Player)
                {
                    target.SetByte(UpdateFields.PlayerFieldBytes2, StealthAuras.PlayerFlagsByte,
                        (byte)(target.GetByte(UpdateFields.PlayerFieldBytes2, StealthAuras.PlayerFlagsByte) & ~GlowFlag));
                }

                if (!spells.HasAuraType(target, AuraType.ModStealth))
                {
                    registry.SetVisibility(target, StealthVisibility.On);
                    target.Map?.RefreshVisibility(target);
                }
            }
        }, null);
    }

    public static AuraHandler DetectionHandler() => new((_, holder, _, _) =>
    {
        // vmangos SpellAuras.cpp:3763-3779: update the player's view when its detection aura changes.
        if (holder.Target is Player player)
        {
            player.Map?.RefreshVisibility(player);
        }
    }, null);

    /// <summary>vmangos Unit.cpp:6502-6541; one matching invisibility type suffices.</summary>
    public static bool CanDetect(SpellSystem spells, Unit viewer, Unit target)
    {
        ArgumentNullException.ThrowIfNull(spells);
        uint targetMask = Mask(spells, target, AuraType.ModInvisibility);
        if (targetMask == 0)
        {
            return true;
        }

        if (viewer is ICombatCreature { IsWorldBoss: true })
        {
            return true;
        }

        if ((targetMask & Mask(spells, viewer, AuraType.ModInvisibility)) != 0)
        {
            return true;
        }

        uint detectMask = Mask(spells, viewer, AuraType.ModInvisibilityDetection);
        foreach (int type in Enumerable.Range(0, 32))
        {
            uint bit = 1u << type;
            if ((targetMask & bit) == 0)
            {
                continue;
            }

            int invisLevel = MaxLevel(spells, target, AuraType.ModInvisibility, type);
            int detectLevel = (detectMask & bit) != 0
                ? MaxLevel(spells, viewer, AuraType.ModInvisibilityDetection, type)
                : 0;
            if (invisLevel <= detectLevel)
            {
                return true;
            }
        }

        return false;
    }

    private static uint Mask(SpellSystem spells, Unit unit, AuraType type)
    {
        uint mask = 0;
        foreach (SpellAura aura in spells.GetAuras(unit).Where(h => !h.IsRemoved).SelectMany(h => h.Auras).OfType<SpellAura>())
        {
            if (aura.Type == type && (uint)aura.MiscValue < 32)
            {
                mask |= 1u << aura.MiscValue;
            }
        }

        return mask;
    }

    private static int MaxLevel(SpellSystem spells, Unit unit, AuraType type, int miscValue) =>
        spells.GetAuras(unit).Where(h => !h.IsRemoved)
            .SelectMany(h => h.Auras).OfType<SpellAura>()
            .Where(a => a.Type == type && a.MiscValue == miscValue)
            .Select(a => a.Amount).DefaultIfEmpty(0).Max();
}

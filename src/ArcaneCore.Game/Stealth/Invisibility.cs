using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Stealth;

/// <summary>vmangos invisibility types and detection levels, derived from authoritative active auras.</summary>
public static class Invisibility
{
    public const byte PlayerGlow = 0x40;

    public static uint Mask(SpellSystem spells, Unit unit, AuraType type)
    {
        uint mask = 0;
        foreach (SpellAura aura in Auras(spells, unit, type))
            if (aura.MiscValue is >= 0 and < 32) mask |= 1u << aura.MiscValue;
        return mask;
    }

    public static bool CanDetect(SpellSystem spells, Unit viewer, Unit target)
    {
        uint mask = Mask(spells, target, AuraType.ModInvisibility);
        if (mask == 0 || viewer is ICombatCreature { IsWorldBoss: true }) return true;
        if ((mask & Mask(spells, viewer, AuraType.ModInvisibility)) != 0) return true;
        for (int type = 0; type < 32; type++)
        {
            if ((mask & (1u << type)) == 0) continue;
            int invisible = Maximum(spells, target, AuraType.ModInvisibility, type);
            int detection = Maximum(spells, viewer, AuraType.ModInvisibilityDetection, type);
            // Player type6 uses drunk value in vmangos rather than aura detection.
            // Alcohol state is not yet modeled; do not invent a positive detection level.
            if (type == 6 && viewer is Player) detection = 0;
            if (invisible <= detection) return true;
        }
        return false;
    }

    public static void Register(SpellSystem spells)
    {
        spells.RegisterAura(AuraType.ModInvisibility, new AuraHandler((system, holder, _, apply) =>
        {
            Unit target = holder.Target;
            if (apply) system.RemoveAurasWithInterruptFlags(target, AuraInterruptMask.StealthInvisibility);
            if (target is Player)
            {
                byte flags = target.GetByte(UpdateFields.PlayerFieldBytes2, StealthAuras.PlayerFlagsByte);
                bool hidden = Mask(system, target, AuraType.ModInvisibility) != 0;
                target.SetByte(UpdateFields.PlayerFieldBytes2, StealthAuras.PlayerFlagsByte,
                    hidden ? (byte)(flags | PlayerGlow) : (byte)(flags & ~PlayerGlow));
            }
            target.Map?.RefreshVisibility(target);
        }, null));
        spells.RegisterAura(AuraType.ModInvisibilityDetection, new AuraHandler((_, holder, _, _) =>
            holder.Target.Map?.RefreshVisibility(holder.Target), null));
    }

    private static int Maximum(SpellSystem spells, Unit unit, AuraType type, int value)
        => Auras(spells, unit, type).Where(aura => aura.MiscValue == value).Select(aura => Math.Max(0, aura.Amount)).DefaultIfEmpty().Max();

    private static IEnumerable<SpellAura> Auras(SpellSystem spells, Unit unit, AuraType type)
        => spells.GetAuras(unit).Where(holder => !holder.IsRemoved)
            .SelectMany(holder => holder.Auras.OfType<SpellAura>()).Where(aura => aura.Type == type);
}

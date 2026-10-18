using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Shaman;

/// <summary>
/// The Earthshatterer 8-piece bonus (28820; vmangos Aura::TriggerSpell, SpellAuras.cpp:1438-1450, and the 18350 dummy trigger, SpellEffects.cpp:900-919):
/// the aura that the Lightning Shield cast puts on the shaman (15 mana per 5 s) checks on every 5 s tick whether a Lightning Shield (CF_SHAMAN_LIGHTNING_SHIELD,
/// bit 10) is still up, and removes itself when none is.
/// </summary>
public sealed class ShamanSetBonusModule : ISpellHandlerModule
{
    public const uint EarthshattererLightningShield = 28820;
    private const uint ShamanFamily = 11;
    private const int LightningShieldBit = 10;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterPeriodicTriggerScript(EarthshattererLightningShield, EarthshattererTick);
    }

    /// <summary>One 5 s tick of 28820: gone when its target has no Lightning Shield left.</summary>
    public static void EarthshattererTick(SpellSystem system, SpellAuraHolder holder, SpellAura aura)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(holder);
        Unit target = holder.Target;
        foreach (SpellAuraHolder other in system.GetAuras(target))
        {
            if (!other.IsRemoved && other.Spell.IsFitToFamily(ShamanFamily, LightningShieldBit))
            {
                return;
            }
        }

        system.RemoveAuras(target, EarthshattererLightningShield);
    }
}
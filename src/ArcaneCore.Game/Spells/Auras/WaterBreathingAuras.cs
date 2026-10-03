using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_AURA_WATER_BREATHING (82) and SPELL_AURA_MOD_WATER_BREATHING (155), after vmangos <c>Aura::HandleWaterBreathing</c> and
/// <c>HandleModWaterBreathing</c> (SpellAuras.cpp:2305-2316). Players only. Water Breathing makes the breath time zero (the player never
/// runs out of breath: the breath timer is not started); without one, Mod Water Breathing auras multiply the breath time by the
/// product of (100 + amount) / 100 (Unending Breath +300% gives four times as long). Removing a Water Breathing aura keeps the
/// multiplier at zero while another Water Breathing aura is still on the player.
/// </summary>
public sealed class WaterBreathingAuras : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.WaterBreathing, new AuraHandler((s, h, a, apply) => Apply(h.Target, a, apply, waterBreathing: true), null));
        system.RegisterAura(AuraType.ModWaterBreathing, new AuraHandler((s, h, a, apply) => Apply(h.Target, a, apply, waterBreathing: false), null));
    }

    private static void Apply(Unit target, SpellAura aura, bool apply, bool waterBreathing)
    {
        if (apply)
        {
            target.Locomotion.Auras.Add(aura);
        }
        else
        {
            target.Locomotion.Auras.Remove(aura);
        }

        if (target is not Player player)
        {
            return;
        }

        AuraLedger auras = player.Locomotion.Auras;

        // HandleWaterBreathing: zero while there is a Water Breathing aura (the one being applied, or another one on removal);
        // HandleModWaterBreathing: the Mod Water Breathing auras alone.
        float multiplier = waterBreathing && (apply || auras.Has(AuraType.WaterBreathing)) ? 0.0f : auras.Multiplier(AuraType.ModWaterBreathing);
        LiquidEnvironment.SetBreathingMultiplier(player, multiplier);
    }
}

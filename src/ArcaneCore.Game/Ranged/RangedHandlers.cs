using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Ranged;

/// <summary>Installs the hunter lane aura and effect handlers into a <see cref="SpellSystem"/> (through the public <see cref="SpellSystem.RegisterAura"/> seam).</summary>
public static class RangedHandlers
{
    public static void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        var tracking = new AuraHandler(static (s, h, a, apply) => TrackingAuras.Apply(s, h, a, apply), null);
        system.RegisterAura(AuraType.TrackCreatures, tracking);
        system.RegisterAura(AuraType.TrackResources, tracking);
        system.RegisterAura(AuraType.TrackStealthed, tracking);
        system.RegisterAura(AuraType.ModStalked, tracking);
        system.RegisterAura(AuraType.FeignDeath, new AuraHandler(static (s, h, a, apply) => s.ApplyFeignDeath(h, a, apply), null));
        system.RegisterEffect(SpellEffectName.SummonObjectSlot1, static c => c.System.EffectSummonObject(c));
        system.RegisterEffect(SpellEffectName.SummonObjectSlot2, static c => c.System.EffectSummonObject(c));
        system.RegisterEffect(SpellEffectName.SummonObjectSlot3, static c => c.System.EffectSummonObject(c));
        system.RegisterEffect(SpellEffectName.SummonObjectSlot4, static c => c.System.EffectSummonObject(c));
    }
}

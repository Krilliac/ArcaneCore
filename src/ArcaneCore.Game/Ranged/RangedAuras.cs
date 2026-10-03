using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Ranged;

/// <summary>Installs the hunter lane's aura handlers into a <see cref="SpellSystem"/> (through the public <see cref="SpellSystem.RegisterAura"/> seam).</summary>
public static class RangedAuras
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
    }
}

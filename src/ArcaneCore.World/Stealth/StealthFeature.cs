using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stealth;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Stealth;

/// <summary>
/// Stealth in the world daemon (discovered <see cref="IWorldFeature"/>, docs/integration/rogue-stealth-core.md): registers the
/// SPELL_AURA_MOD_STEALTH, MOD_STEALTH_LEVEL, MOD_STEALTH_DETECT and invisibility handlers on the world's spell system and attaches, to every
/// map, the stealth visibility rule and the 2000 ms detection pass (<see cref="StealthDetectionUpdater"/>).
/// <para>
/// The shapeshift half of a Stealth rank (SPELL_AURA_MOD_SHAPESHIFT, form 30) and the movement slow (aura 33) belong to the stance
/// and speed-aura lanes; without them the unit carries the stealth auras but is not in form 30 (docs/areas/rogue.md, limits).
/// </para>
/// </summary>
public sealed class StealthFeature(SpellFeature spells, IServiceProvider services) : IWorldFeature
{
    /// <summary>The stealthed units (visibility groups).</summary>
    public StealthRegistry Registry { get; } = new();

    /// <summary>The bound settings (World:Stealth; every default is the vmangos value).</summary>
    public StealthOptions Options { get; } = new();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        services.GetService<IConfiguration>()?.GetSection(StealthOptions.SectionName).Bind(Options);

        SpellSystem system = spells.System;
        system.ImprovedSapRollPerPhase = Options.ImprovedSapRollPerPhase;
        var stealthServices = new StealthServices(system, Registry, Options);
        system.RegisterAura(AuraType.ModStealth, StealthAuras.Handler(Registry));
        system.RegisterAura(AuraType.ModInvisibility, InvisibilityAuras.InvisibilityHandler(Registry));
        system.RegisterAura(AuraType.ModInvisibilityDetection, InvisibilityAuras.DetectionHandler());
        // Data auras: their amounts are read by the detection formula (SpellSystem.GetTotalAuraModifier), nothing happens at apply.
        system.RegisterAura(AuraType.ModStealthLevel, new AuraHandler(null, null));
        system.RegisterAura(AuraType.ModStealthDetect, new AuraHandler(null, null));

        void Install(Map map)
        {
            StealthServices.Install(map, stealthServices);
            map.AddVisibilityRule(new StealthVisibilityRule(system, Registry, Options));
            map.AddUpdater(new StealthDetectionUpdater(Registry, unit => !unit.IsInWorld && !system.IsInTransit(unit)));
        }

        world.MapCreated += Install;
        foreach (Map map in world.Maps)
        {
            Install(map);
        }
    }
}

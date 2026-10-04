using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Mods;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells.Mods;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Configures the spell-modifier engine the built-in <see cref="SpellModModule"/> installed (docs/areas/spell-mods.md): binds the
/// <c>Spells:Mods</c> section onto its options, loads the class-mask overlay file when one is configured, and logs how many
/// modifier effects have no class mask. It sits in <c>ArcaneCore.World.Spells</c> so it attaches after <see cref="SpellFeature"/>,
/// which loads the spell store. The section is restart-only: <c>.reload config</c> does not re-read it.
/// </summary>
public sealed class SpellModFeature(IServiceProvider services, ILogger<SpellModFeature> logger) : IWorldFeature
{
    /// <summary>Read <c>Spells:Mods</c> from <paramref name="configuration"/> onto <paramref name="options"/>; absent keys keep the retail defaults.</summary>
    public static void BindOptions(IConfiguration? configuration, SpellModOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        configuration?.GetSection(SpellModOptions.SectionName).Bind(options);
    }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        SpellSystem system = services.GetRequiredService<SpellFeature>().System;
        ISpellModEngine engine = system.Mods;
        BindOptions(services.GetService<IConfiguration>(), engine.Options);

        if (!string.IsNullOrWhiteSpace(engine.Options.ClassMaskFile))
        {
            FileClassMaskSource source = FileClassMaskSource.Load(engine.Options.ClassMaskFile);
            engine.MaskSource = source;
            logger.LogInformation("Spell modifiers: class mask overlay {File}: {Rows} row(s), {Wide} above 32 bits", engine.Options.ClassMaskFile, source.Count, source.WideCount);
        }

        int effects = 0;
        int withoutMask = 0;
        foreach (SpellInfo spell in system.Store.All)
        {
            for (int i = 0; i < spell.Effects.Count; i++)
            {
                SpellEffectInfo effect = spell.Effects[i];
                if (effect.Effect == SpellEffectName.ApplyAura && effect.AuraType is AuraType.AddFlatModifier or AuraType.AddPctModifier)
                {
                    effects++;
                    if (engine.ClassMask(spell, i) == 0)
                    {
                        withoutMask++;
                    }
                }
            }
        }

        if (withoutMask > 0 && engine.MaskSource is null)
        {
            logger.LogWarning(
                "Spell modifiers: {Without} of {Effects} modifier effect(s) have an empty class mask and affect no spell; the spell data holds 32-bit masks only, "
                + "set Spells:Mods:ClassMaskFile to a file written by 'arcane-content-importer class-masks'", withoutMask, effects);
        }
        else
        {
            logger.LogInformation("Spell modifiers: {Effects} modifier effect(s), {Without} with an empty class mask", effects, withoutMask);
        }
    }
}

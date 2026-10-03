using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Application;
using ArcaneCore.Game.Spells.Rules.Diminishing;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Installs the retail spell combat rules (docs/areas/spell-rules.md): binds the <c>SpellRules</c> options
/// section, replaces the default <see cref="VanillaSpellCombatRules"/> with one that uses those options (unless
/// another area registered its own <see cref="ISpellCombatRules"/>), and adds the application rules (per-effect
/// mechanic resistance, diminishing returns). It sits in <c>ArcaneCore.World.Spells</c> so it attaches after
/// <see cref="SpellFeature"/>, which creates the spell system and installs the default rules.
/// </summary>
public sealed class SpellRulesCoreFeature(IServiceProvider services, ILogger<SpellRulesCoreFeature> logger) : IWorldFeature
{
    /// <summary>The bound options (defaults until <see cref="Attach"/>).</summary>
    public SpellRuleOptions Options { get; private set; } = new();

    /// <summary>Read <c>SpellRules</c> from <paramref name="configuration"/>; absent keys keep the retail defaults.</summary>
    public static SpellRuleOptions BindOptions(IConfiguration? configuration)
    {
        var options = new SpellRuleOptions();
        configuration?.GetSection(SpellRuleOptions.SectionName).Bind(options);
        return options;
    }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        Options = BindOptions(services.GetService<IConfiguration>());
        SpellSystem system = services.GetRequiredService<SpellFeature>().System;

        if (services.GetService<ISpellCombatRules>() is null)
        {
            system.CombatRules = new VanillaSpellCombatRules { Options = Options };
        }

        system.ApplicationRules.Add(new MechanicResistRule());
        if (Options.DiminishingReturns)
        {
            new DiminishingRule(Options.DiminishingResetMs).Attach(system);
        }

        logger.LogInformation(
            "Spell rules: magic hit floor {Floor}%, boss level diff {Diff}, holy resistance {Holy}, diminishing returns {Dr}",
            Options.MagicHitFloorPercent, Options.WorldBossLevelDiff, Options.IgnoreHolyResistance ? "ignored" : "resisted", Options.DiminishingReturns ? "on" : "off");
    }
}

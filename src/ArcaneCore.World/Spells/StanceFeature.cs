using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Warrior stances in the world daemon (discovered <see cref="IWorldFeature"/>): installs the
/// SPELL_AURA_MOD_SHAPESHIFT handler and the stance cast gate (<see cref="ShapeshiftService"/>). The form table
/// is the client's SpellShapeshiftForm.dbc when <c>Combat:ShapeshiftFormDbcPath</c> is set (a bad file stops the
/// daemon), otherwise the three warrior stances (<see cref="ShapeshiftFormCatalog.WarriorStances"/>, logged).
/// </summary>
public sealed class StanceFeature(IServiceProvider services, ILogger<StanceFeature> logger) : IWorldFeature
{
    public CombatOptions Options { get; } = new();

    public ShapeshiftService? Service { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        services.GetService<IConfiguration>()?.GetSection(CombatOptions.SectionName).Bind(Options);

        ShapeshiftFormCatalog forms;
        if (string.IsNullOrWhiteSpace(Options.ShapeshiftFormDbcPath))
        {
            forms = ShapeshiftFormCatalog.WarriorStances;
            logger.LogWarning("Combat:ShapeshiftFormDbcPath is not set: only the three warrior stances are known (SpellShapeshiftForm.dbc gives every form)");
        }
        else
        {
            forms = ShapeshiftFormDbcReader.Load(Options.ShapeshiftFormDbcPath);
            logger.LogInformation("Loaded {Forms} shapeshift forms from {Path}", forms.Count, Options.ShapeshiftFormDbcPath);
        }

        SpellFeature spells = services.GetRequiredService<SpellFeature>();
        Service = new ShapeshiftService(spells.System, forms, Options, player => spells.Spellbook.GetSpells(player), logger);
        Service.Install();
    }
}

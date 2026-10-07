using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Warrior stances in the world daemon (discovered <see cref="IWorldFeature"/>): installs the
/// SPELL_AURA_MOD_SHAPESHIFT handler and the stance cast gate (<see cref="ShapeshiftService"/>). The form table
/// is the client's SpellShapeshiftForm.dbc when <c>Combat:ShapeshiftFormDbcPath</c> is set (a bad file stops the
/// daemon), otherwise the built-in build-5875 rows (<see cref="ShapeshiftFormCatalog.Retail"/>); with
/// <c>Combat:RequireShapeshiftFormDbc</c> a missing path stops the daemon. The table is also linked to the combat
/// environment (<see cref="CombatEnvironment.ShapeshiftForms"/>) for the flag-based form predicates.
/// </summary>
public sealed class StanceFeature(IServiceProvider services, ILogger<StanceFeature> logger) : IWorldFeature
{
    public CombatOptions Options { get; private set; } = new();

    public ShapeshiftService? Service { get; private set; }

    private readonly List<IFormChangeListener> _listeners = [];

    /// <summary>
    /// Be told after every form change (see <see cref="IFormChangeListener"/>). Safe to call before or after
    /// <see cref="Attach"/>, so features do not depend on the order they attach in.
    /// </summary>
    public void AddFormChangeListener(IFormChangeListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _listeners.Add(listener);
        Service?.AddListener(listener);
    }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        CombatEnvironment environment = CombatEnvironments.GetOrCreate(services, world, logger);
        Options = environment.Options;

        ShapeshiftFormCatalog forms;
        if (services.GetService<ShapeshiftFormCatalog>() is { } supplied)
        {
            forms = supplied;
        }
        else if (string.IsNullOrWhiteSpace(Options.ShapeshiftFormDbcPath))
        {
            if (Options.RequireShapeshiftFormDbc)
            {
                throw new InvalidOperationException("Combat:RequireShapeshiftFormDbc is set but Combat:ShapeshiftFormDbcPath is not");
            }

            forms = ShapeshiftFormCatalog.Retail;
            logger.LogInformation("Combat:ShapeshiftFormDbcPath is not set: using the built-in build-5875 SpellShapeshiftForm table ({Forms} rows)", forms.Count);
        }
        else
        {
            forms = ShapeshiftFormDbcReader.Load(Options.ShapeshiftFormDbcPath);
            logger.LogInformation("Loaded {Forms} shapeshift forms from {Path}", forms.Count, Options.ShapeshiftFormDbcPath);
        }

        environment.ShapeshiftForms = forms;
        SpellFeature spells = services.GetRequiredService<SpellFeature>();
        Service = new ShapeshiftService(spells.System, forms, Options, player => spells.Spellbook.GetSpells(player), logger);
        Service.Install();
        foreach (IFormChangeListener listener in _listeners)
        {
            Service.AddListener(listener);
        }
    }
}

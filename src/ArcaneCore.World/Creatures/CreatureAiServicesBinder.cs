using System.Reflection;
using ArcaneCore.Data.Npc;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Creatures;

/// <summary>
/// Builds the <see cref="CreatureAiServices"/> every map's creature system uses.
/// <para>
/// First the built-in defaults (hostility from the registered <see cref="ICreatureHostility"/> or
/// FactionTemplate.dbc; the spell system adapter when the <see cref="SpellFeature"/> is registered; a
/// fresh <see cref="CreatureAiFactory"/>). Then every public settable property of
/// <see cref="CreatureAiServices"/> is bound by reflection from the service provider: a registered
/// service of the property's type replaces the default. A later AI slice therefore adds a seam by
/// adding a property to <see cref="CreatureAiServices"/> and never edits the creature feature (the
/// same discovery pattern as the other seams, docs/integration/seams.md). Properties whose type is
/// not registered keep their default.
/// </para>
/// </summary>
public static class CreatureAiServicesBinder
{
    private static readonly PropertyInfo[] Bindable = [.. typeof(CreatureAiServices)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.SetMethod is { IsPublic: true })];

    public static CreatureAiServices Build(IServiceProvider services, CreatureOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ICreatureHostility hostility = services.GetService<ICreatureHostility>()
            ?? new FactionCreatureHostility(services.GetService<FactionTemplateCatalog>()
                ?? (string.IsNullOrWhiteSpace(options.FactionTemplateDbcPath)
                    ? FactionTemplateCatalog.Empty
                    : FactionTemplateDbcReader.Load(options.FactionTemplateDbcPath)));
        SpellFeature? spells = services.GetService<SpellFeature>();
        var result = new CreatureAiServices
        {
            Hostility = hostility,
            Spells = spells is null ? null : new SpellSystemCreatureCaster(spells.System),
            Factory = services.GetService<CreatureAiFactory>() ?? new CreatureAiFactory(),
        };

        foreach (PropertyInfo property in Bindable)
        {
            Type serviceType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (services.GetService(serviceType) is { } registered)
            {
                property.SetValue(result, registered);
            }
        }

        return result;
    }
}

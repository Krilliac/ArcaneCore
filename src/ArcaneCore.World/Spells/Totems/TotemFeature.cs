using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Totems;
using ArcaneCore.Kernel.WorldData.Totems;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Spells.Totems;

/// <summary>
/// The shaman totem system in the world daemon (discovered <see cref="IWorldFeature"/>). Its full type
/// name sorts after both <see cref="SpellFeature"/> and <see cref="CreatureWorldFeature"/>, which it needs
/// attached first: it installs the totem effect handlers on the spell system, decorates the spell system's
/// group resolver so a totem resolves its owner's party, loads <c>totem_spell</c> and attaches the totem
/// updater to every map. Options come from the <c>Totems</c> configuration section (<see cref="TotemOptions"/>).
/// <para>
/// <c>Totems:Enabled=false</c> leaves the effects unregistered: they then report "not implemented" and no totem
/// is ever summoned (fail closed).
/// </para>
/// </summary>
public sealed class TotemFeature(IServiceProvider services, ILogger<TotemFeature> logger) : IWorldFeature
{
    public TotemOptions Options { get; } = new();

    /// <summary>The totem system; null until attached or when disabled.</summary>
    public TotemSystem? System { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        services.GetService<IConfiguration>()?.GetSection(TotemOptions.SectionName).Bind(Options);
        SpellFeature spells = services.GetRequiredService<SpellFeature>();
        CreatureWorldFeature creatures = services.GetRequiredService<CreatureWorldFeature>();

        TotemContent content = TotemContent.Empty;
        using (IServiceScope scope = services.CreateScope())
        {
            if (scope.ServiceProvider.GetService<ITotemDataStore>() is { } store)
            {
                content = store.LoadAsync().GetAwaiter().GetResult();
            }
        }

        var system = new TotemSystem(
            spells.System,
            map => creatures.GetOrCreateSystem(map),
            entry => creatures.Content.FindTemplate(entry),
            content.GetSpell,
            Options,
            logger);
        if (!system.Register())
        {
            logger.LogInformation("Totems are disabled (Totems:Enabled=false); the totem spell effects stay unregistered");
            return;
        }

        spells.System.Groups = new OwnerAwareGroupResolver(spells.System.Groups);
        System = system;
        logger.LogInformation("Loaded {Count} totem spells", content.Count);
        if (content.Count == 0)
        {
            logger.LogWarning("The totem_spell table is empty: summoned totems have no passive aura or active spell. Run ArcaneCore.ContentImporter import against the classic-db dump to fill it");
        }
        world.MapCreated += system.EnsureUpdater;
        world.Post(() =>
        {
            foreach (Map map in world.Maps)
            {
                system.EnsureUpdater(map);
            }
        });
    }
}

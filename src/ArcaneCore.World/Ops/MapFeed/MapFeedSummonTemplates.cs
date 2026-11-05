using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.Kernel.WorldData.Totems;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Spells.Totems;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// The static set of creature templates that are summons: the <c>summonTemplates</c> array of the <c>caps</c> answer (docs/areas/atlas-public.md,
/// "Guest data policy"). The public Atlas listener uses it as its template set when the world's <c>links</c> answer has never been available, so a
/// pet species that first appears during an outage is still recognised.
/// <para>
/// A template is in the set when it is a totem or non-combat pet by creature type (CreatureType.dbc 11 and 12), a row of <c>totem_spell</c>
/// (<see cref="TotemContent"/>), or a creature with pet level stats or create spells (<see cref="PetContent"/>; this includes tameable beasts, so a
/// wild creature of such an entry is dropped too when the listener fails closed, which is the safe side). Pure data: content is immutable once loaded,
/// so the answer is the same on every ask until a content reload, and the function needs no world thread.
/// </para>
/// </summary>
internal static class MapFeedSummonTemplates
{
    /// <summary>CreatureType.dbc: totem.</summary>
    public const uint TotemCreatureType = 11;

    /// <summary>CreatureType.dbc: non-combat pet.</summary>
    public const uint NonCombatPetCreatureType = 12;

    /// <summary>The set from the three content tables, sorted ascending and without repeats. Any table may be null.</summary>
    public static uint[] Compute(CreatureContent? creatures, TotemContent? totems, PetContent? pets)
    {
        var entries = new SortedSet<uint>();
        if (creatures is not null)
        {
            foreach (CreatureTemplate template in creatures.Templates)
            {
                if (template.CreatureType is TotemCreatureType or NonCombatPetCreatureType)
                {
                    entries.Add(template.Entry);
                }
            }
        }

        if (totems is not null)
        {
            entries.UnionWith(totems.Entries);
        }

        if (pets is not null)
        {
            entries.UnionWith(pets.Entries);
        }

        return [.. entries];
    }

    /// <summary>The set from the loaded features of <paramref name="services"/>; a feature that is not attached contributes nothing.</summary>
    public static uint[] Compute(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return Compute(
            services.GetService<CreatureWorldFeature>()?.Content,
            services.GetService<TotemFeature>()?.Content,
            services.GetService<PetsFeature>()?.Service.Content);
    }
}

using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells.Casters;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Spells.Casters;

/// <summary>
/// Installs the mage / priest / warlock spell rules (<see cref="CasterSpellModules"/>) on the world's
/// <see cref="SpellFeature.System"/>, with the <c>Spells:Casters</c> settings. The spell system object exists from
/// the feature's construction, so the registration does not depend on the order in which discovered features attach.
/// </summary>
public sealed class CasterFeature(SpellFeature spells, IServiceProvider services) : IWorldFeature
{
    /// <summary>The bound settings (filled by <see cref="Attach"/>).</summary>
    public CasterOptions Options { get; } = new();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        services.GetService<IConfiguration>()?.GetSection(CasterOptions.SectionName).Bind(Options);
        CasterSpellModules.Register(spells.System, Options);
    }
}

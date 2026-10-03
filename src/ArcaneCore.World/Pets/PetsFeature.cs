using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Pets;

/// <summary>
/// Pets, guardians, mini pets and totems in the world daemon (a discovered <see cref="IWorldFeature"/>,
/// docs/integration/pets.md). It owns the <see cref="SummonService"/> and is itself the
/// <see cref="ISpellSummonSink"/> the spell feature picks up from the container (the seam list in
/// <see cref="WorldFeatures"/> registers it), so SPELL_EFFECT_SUMMON and the quest reward preflight
/// reach the same code as a cast. On attach it installs the totem effects on the spell system and
/// hands the <c>Pets</c> configuration (<see cref="PetOptions"/>) to every map's pet system.
/// <para>The service exists from construction, so the order in which features attach does not matter.</para>
/// </summary>
public sealed class PetsFeature : IWorldFeature, ISpellSummonSink
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PetsFeature> _logger;

    public PetsFeature(IServiceProvider services, ILogger<PetsFeature> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _logger = logger;
        Service = new SummonService(
            Options,
            map => _services.GetService<CreatureWorldFeature>()?.GetOrCreateSystem(map),
            logger);
        Controller = new PetController(Service, () => _services.GetService<SpellFeature>()?.System);
    }

    /// <summary>Tuning bound from the <c>Pets</c> section (retail defaults).</summary>
    public PetOptions Options { get; } = new();

    /// <summary>The summon service every map uses.</summary>
    public SummonService Service { get; }

    /// <summary>What the client can ask of its pets (the pet opcodes call it).</summary>
    public PetController Controller { get; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _services.GetService<IConfiguration>()?.GetSection(PetOptions.SectionName).Bind(Options);

        // The pet tables: without a registered store the world simply has no pet data (templates are used).
        using (IServiceScope scope = _services.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IPetDataStore>() is { } store)
            {
                Service.Content = store.LoadAsync().GetAwaiter().GetResult();
                _logger.LogInformation("Loaded {Stats} pet level stat sets and {Spells} pet spell lists",
                    Service.Content.LevelStatsEntryCount, Service.Content.CreateSpellEntryCount);
            }
        }

        if (_services.GetService<SpellFeature>() is { } spells)
        {
            Service.Install(spells.System);
        }

        world.MapCreated += ApplyOptions;
        foreach (Map map in world.Maps)
        {
            ApplyOptions(map);
        }
    }

    private void ApplyOptions(Map map)
    {
        if (map.Pets is { } pets)
        {
            pets.Options = Options;
        }
    }

    // --- ISpellSummonSink -----------------------------------------------------------------------

    public Unit? Summon(Unit caster, uint entry, float x, float y, float z, float orientation, int durationMs)
        => Service.Summon(caster, entry, x, y, z, orientation, durationMs);

    public Unit? Summon(Unit caster, in SpellSummonRequest request) => Service.Summon(caster, in request);

    public bool CanSummon(Unit owner, uint entry) => Service.CanSummon(owner, entry);
}

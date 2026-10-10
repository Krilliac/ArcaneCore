using ArcaneCore.Data.Npc;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Pets;

/// <summary>
/// Pets, guardians, mini pets and wild summons in the world daemon (totems are the shaman lane's TotemFeature) (a discovered <see cref="IWorldFeature"/>,
/// docs/integration/pets.md). It owns the <see cref="SummonService"/> and is itself the
/// <see cref="ISpellSummonSink"/> the spell feature picks up from the container (the seam list in
/// <see cref="WorldFeatures"/> registers it), so SPELL_EFFECT_SUMMON and the quest reward preflight
/// reach the same code as a cast. On attach it installs the summon effects on the spell system and
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

        IReadOnlyDictionary<uint, uint>? familySkillLines = null;
        if (!string.IsNullOrWhiteSpace(Options.CreatureFamilyDbcPath))
        {
            familySkillLines = global::ArcaneCore.Data.Content.Pets.CreatureFamilyDbcReader.LoadSkillLines(Options.CreatureFamilyDbcPath);
            IReadOnlyDictionary<uint, uint> masks = global::ArcaneCore.Data.Content.Pets.CreatureFamilyDbcReader.LoadFoodMasks(Options.CreatureFamilyDbcPath);
            Service.PetFoodMask = family => masks.TryGetValue(family, out uint mask) ? mask : null;
            _logger.LogInformation("Loaded {Families} pet diets from {Path}", masks.Count, Options.CreatureFamilyDbcPath);
        }
        else
        {
            _logger.LogInformation("Pets:CreatureFamilyDbcPath is not set: Feed Pet skips the pet diet check");
        }

        if (_services.GetService<SpellFeature>() is { } spells)
        {
            Service.Install(spells.System);
            Service.Training = BuildTraining(familySkillLines, spells.System);
        }

        world.MapCreated += ApplyOptions;
        foreach (Map map in world.Maps)
        {
            ApplyOptions(map);
        }

        // What the service remembers of a player (the spirit healer's pet, a temporarily unsummoned pet) goes with the session.
        world.PlayerLoggingOut += Service.ForgetOwner;

        // The pet goes with its owner through teleports (vmangos UnsummonPetTemporaryIfAny / ResummonPetTemporaryUnSummonedIfAny). Features
        // attach in type-name order and the teleport service exists once TeleportFeature attached, so it is looked up on the world thread.
        world.Post(() =>
        {
            if (_services.GetService<TeleportFeature>() is { } teleports)
            {
                TeleportFollow = new PetTeleportFollow(Service, teleports.Teleports, world);
            }
        });
    }

    /// <summary>
    /// Beast training: the SkillLineAbility catalog (DI, else NpcServices:SkillLineAbilityDbcPath, as TalentFeature.BuildRankChain; the NpcServices
    /// options bind lazily so the section is read directly) with its reqtrainpoints, and the family skill lines. Null, logged once, when either is missing.
    /// </summary>
    private PetTraining? BuildTraining(IReadOnlyDictionary<uint, uint>? familySkillLines, SpellSystem spells)
    {
        SkillLineAbilityCatalog? abilities = _services.GetService<SkillLineAbilityCatalog>();
        if (abilities is null && _services.GetService<IConfiguration>()?[NpcServiceOptions.SectionName + ":" + nameof(NpcServiceOptions.SkillLineAbilityDbcPath)]
            is { Length: > 0 } path)
        {
            abilities = NpcServiceDbcReaders.LoadSkillLineAbilities(path);
        }

        if (abilities is not { HasTrainingPoints: true } || familySkillLines is null)
        {
            _logger.LogInformation("Pet training-point costs and family checks are off: needs Pets:CreatureFamilyDbcPath and a 15-field SkillLineAbility.dbc");
            return null;
        }

        _logger.LogInformation("Pet training enabled: {Abilities} skill line spells, {Families} families", abilities.Count, familySkillLines.Count);
        return new PetTraining(abilities, familySkillLines, id => spells.Store.Get(id));
    }

    /// <summary>The teleport hook of the pets (after the world's first tick; null without a teleport feature).</summary>
    public PetTeleportFollow? TeleportFollow { get; private set; }

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

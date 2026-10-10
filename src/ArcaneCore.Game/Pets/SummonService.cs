using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// Summoning (vmangos Spell::EffectSummon, EffectSummonWild, EffectSummonGuardian, EffectSummonCritter): the production
/// <see cref="ISpellSummonSink"/>. The totem effects belong to the shaman lane's <c>TotemSystem</c> (wave-2 integration). One instance serves every
/// map; the per-map state (totem slots, timers) lives in <see cref="PetMapSystem"/> and the
/// creature itself in the map's <see cref="CreatureMapSystem"/>.
/// <para>
/// Install it on a <see cref="SpellSystem"/> with <see cref="Install"/>: the totem effects
/// (SUMMON_TOTEM and the four slot effects) are registered, and SPELL_EFFECT_SUMMON keeps its
/// built-in handler, which now reaches this service through the sink, so the quest reward
/// preflight (<see cref="CanSummon"/>) models exactly what a cast does.
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed partial class SummonService : ISpellSummonSink
{
    private readonly PetOptions _options;
    private readonly Func<Map, CreatureMapSystem?> _systems;
    private readonly ILogger _logger;
    private readonly Random _random;
    private readonly HashSet<string> _warned = [];
    private SpellSystem? _spells;
    private uint _petNumbers;
    private bool _summonPetRegistered;
    private bool _demonsInstalled;

    /// <param name="options">Tuning (retail defaults when null); handed to every map's <see cref="PetMapSystem"/>.</param>
    /// <param name="systems">The creature system a summon spawns into; the default is the one attached to the map.</param>
    public SummonService(PetOptions? options = null, Func<Map, CreatureMapSystem?>? systems = null, ILogger? logger = null, Random? random = null)
    {
        _options = options ?? new PetOptions();
        _systems = systems ?? (static map => map.FindUpdater<CreatureMapSystem>());
        _logger = logger ?? NullLogger.Instance;
        _random = random ?? new Random();
    }

    /// <summary>The pet tables (<c>pet_levelstats</c>, <c>petcreateinfo_spell</c>); empty until the world loads them.</summary>
    public PetContent Content { get; set; } = PetContent.Empty;

    /// <summary>Register the summon effects on <paramref name="spells"/>.</summary>
    public void Install(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        _spells = spells;
        spells.RegisterEffect(SpellEffectName.SummonWild, EffectSummonWild);
        spells.RegisterEffect(SpellEffectName.SummonGuardian, EffectSummonGuardian);
        spells.RegisterEffect(SpellEffectName.SummonCritter, EffectSummonCritter);
        spells.RegisterEffect(SpellEffectName.SummonDemon, EffectSummonDemon); // SummonService.SummonDemon.cs
        RegisterSummonPet(spells);
        InstallTaming(spells);
        Charms.Install(spells);
        spells.RegisterEffect(SpellEffectName.SummonDeadPet, context =>
        {
            if (context.Caster is Player player)
            {
                SummonDeadPet(player, context.Value, context.System);
            }
        });
        spells.PlayerSpiritHealed += player => AutoReSummonPet(player); // Spell::EffectSpiritHeal → Player::AutoReSummonPet
        spells.RegisterEffect(SpellEffectName.DismissPet, context =>
        {
            if (context.Caster is Player player && player.Class == Class.Hunter
                && player.Map?.FindObject(player.PetGuid) is Creature { Summon.Kind: SummonKind.Pet } pet && pet.IsAlive
                && pet.OwnerGuid == player.Guid
                && (!DetachedPersistenceConfigured || DetachedPersistenceSupported))
            {
                QueueDetachedPetSave(player);
                Unsummon(pet);
            }
        });
    }

    /// <summary>
    /// SPELL_EFFECT_SUMMON_PET (56), one handler for both halves of vmangos <c>Spell::EffectSummonPet</c>: entry 0 is a hunter's Call Pet (the current
    /// stable pet, <see cref="TryCallCurrentHunterPet"/>); any other entry is a warlock demon, served once <see cref="InstallDemons"/> ran.
    /// </summary>
    private void RegisterSummonPet(SpellSystem spells)
    {
        if (_summonPetRegistered)
        {
            return;
        }

        if (spells.HasEffectHandler(SpellEffectName.SummonPet))
        {
            throw new InvalidOperationException("SPELL_EFFECT_SUMMON_PET already has a handler");
        }

        spells.RegisterEffect(SpellEffectName.SummonPet, context =>
        {
            if (context.Effect.MiscValue == 0)
            {
                if (context.Caster is Player player && player.Class == Class.Hunter)
                {
                    TryCallCurrentHunterPet(player, context.System);
                }
            }
            else if (_demonsInstalled)
            {
                EffectSummonPet(context);
            }
        });
        _summonPetRegistered = true;
    }

    // --- unsummon -------------------------------------------------------------------------------

    /// <summary>
    /// Remove a summon from the world: the owner's pet link for a pet (vmangos Pet::Unsummon),
    /// then the creature itself.
    /// </summary>
    public void Unsummon(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.Summon is not { } links)
        {
            return;
        }

        Unit? owner = creature.GetOwner();
        Map? map = creature.Map;

        // vmangos Pet::Unsummon (Pet.cpp:1118-1136): a charm on the pet ends, and a player possessing it gets its control back.
        Charms.OnUnsummon(creature);
        map?.Combat.CombatStop(creature);
        switch (links.Kind)
        {
            case SummonKind.Pet:
                ReleasePetLink(creature, owner);
                break;
        }

        map?.Pets?.Forget(creature);
        creature.System?.Despawn(creature);
    }

    /// <summary>
    /// The owner side of Pet::Unsummon: Player::RemovePetActionBar for a controlled pet (Pet.cpp:1085)
    /// and clearing the owner's pet GUID (UNIT_FIELD_SUMMON) when it still points at this pet.
    /// </summary>
    internal static void ReleasePetLink(Creature pet, Unit? owner)
    {
        if (owner is Player player)
        {
            player.Session.Send(WorldOpcode.SmsgPetSpells, PetPackets.BuildRemoveActionBar());
        }

        if (owner is not null && owner.PetGuid == pet.Guid)
        {
            owner.SetPetGuid(ObjectGuid.Empty);
        }
    }

    // --- pets (SPELL_EFFECT_SUMMON through the sink) -------------------------------------------------

    /// <inheritdoc/>
    public Unit? Summon(Unit caster, uint entry, float x, float y, float z, float orientation, int durationMs)
        => Summon(caster, new SpellSummonRequest(0, entry, x, y, z, orientation, durationMs));

    /// <inheritdoc/>
    public Unit? Summon(Unit caster, in SpellSummonRequest request)
    {
        ArgumentNullException.ThrowIfNull(caster);

        // vmangos Spell::EffectSummon: a caster that already has a pet summons nothing.
        if (!caster.PetGuid.IsEmpty || !TryGetSystems(caster, request.SpellId, out PetMapSystem? pets, out CreatureMapSystem? creatures))
        {
            return null;
        }

        if (creatures.Content.FindTemplate(request.Entry) is not { } template)
        {
            Warn($"pet-template:{request.Entry}", "creature entry {Entry} not found for spell {Spell}", request.Entry, request.SpellId);
            return null;
        }

        SpellSummonRequest req = request;
        uint petNumber = NextPetNumber();
        Creature pet = creatures.SpawnSummoned(template, HighGuid.Pet, creature =>
        {
            creature.Summon = new SummonLinks(SummonKind.Pet, caster.Guid, req.SpellId, TotemSlots.None, req.DurationMs);
            ApplyOwner(creature, caster, req.SpellId);
            InitPet(creature, SummonKind.Pet, caster, petNumber);
            creature.SetUInt32(UpdateFields.UnitFieldPetexperience, 0);
            creature.SetUInt32(UpdateFields.UnitFieldPetnextlevelexp, 1000);
            creature.NpcFlags = 0;

            // Pet::InitStatsForLevel(caster level, caster): level, flags and the pet_levelstats stats
            PetInitializer.InitStatsForLevel(creature, caster, caster.Level, Content);

            // vmangos passes -caster orientation for the pet (SpellEffects.cpp:2372).
            return new CreatureHome(req.X, req.Y, req.Z, Creature.NormalizeOrientation(-caster.Orientation));
        }, petNumber);

        pets.Options = _options;
        pets.Register(pet, this);
        AttachPetAi(pet);
        PetInitializer.InitCreateSpells(pet, Content, _spells);
        if (pet.Summon?.Charm?.RenameAllowed == true)
        {
            pet.UnitFlags |= UnitFlags.PetRename | UnitFlags.PetAbandon;
        }
        caster.SetPetGuid(pet.Guid);

        // Player::PetSpellInitialize (SpellEffects.cpp:2417-2420)
        if (caster is Player owner)
        {
            owner.Session.Send(WorldOpcode.SmsgPetSpells, PetPackets.BuildPetSpells(pet, pet.Summon!.Charm!, listSpells: true));
        }

        return pet;
    }

    /// <inheritdoc/>
    public bool CanSummon(Unit owner, uint entry)
        => owner.Map is { } map && _systems(map) is { } creatures && creatures.Content.FindTemplate(entry) is not null
            && map.Pets is not null && owner.PetGuid.IsEmpty;

    // --- shared pieces --------------------------------------------------------------------------

    /// <summary>
    /// The links every summoned pet, guardian and mini pet gets from its owner (SpellEffects.cpp:2391-2397,
    /// 2880-2884, 5440-5446): owner and creator, the owner's faction, the pet name timestamp and the spell.
    /// Level, NPC flags and the PvP flag differ per kind and are set by the caller.
    /// </summary>
    internal static void ApplyOwner(Creature creature, Unit owner, uint spellId)
    {
        creature.SetOwnerGuid(owner.Guid);
        creature.SetCreatorGuid(owner.Guid);
        creature.FactionTemplate = owner.FactionTemplate;
        creature.SetUInt32(UpdateFields.UnitFieldPetNameTimestamp, 0);
        creature.SetUInt32(UpdateFields.UnitCreatedBySpell, spellId);
    }

    /// <summary>vmangos CreatureAISelector: a pet (every Pet object) gets PetAI instead of the creature's AIName AI.</summary>
    internal void AttachPetAi(Creature pet) => pet.AI = new PetAI(pet, () => _spells, _random);

    /// <summary>vmangos ObjectMgr::GeneratePetNumber: the pet number a summoned pet is named by (the GUID carries it).</summary>
    internal uint NextPetNumber() => Interlocked.Increment(ref _petNumbers);

    /// <summary>A fresh pet number above <paramref name="atLeast"/> (a stored maximum), for a loaded character dump (vmangos PlayerDumpReader).</summary>
    public uint ReservePetNumberAbove(uint atLeast)
    {
        ReservePetNumber(atLeast);
        return NextPetNumber();
    }

    /// <summary>Keep subsequently generated pet GUID counters above a loaded durable pet.</summary>
    internal void ReservePetNumber(uint petNumber)
    {
        uint current;
        do
        {
            current = Volatile.Read(ref _petNumbers);
            if (current >= petNumber)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _petNumbers, petNumber, current) != current);
    }

    /// <summary>
    /// What vmangos <c>Pet::Pet</c> and <c>Pet::Create</c> give every pet, guardian and mini pet:
    /// a charminfo (a mini pet is always passive, a guardian always aggressive, a summoned pet defensive
    /// for a player owner and aggressive otherwise, SpellEffects.cpp:2400-2404), the pet number, the
    /// pet's misc byte flags and, for a mini pet, immunity to players and creatures (always non-attackable,
    /// Pet.cpp:2266-2268).
    /// </summary>
    internal static void InitPet(Creature creature, SummonKind kind, Unit owner, uint petNumber)
    {
        ReactState react = kind switch
        {
            SummonKind.MiniPet => ReactState.Passive,
            SummonKind.Guardian => ReactState.Aggressive,
            _ => owner is Player ? ReactState.Defensive : ReactState.Aggressive,
        };
        creature.Summon!.Charm = new CharmInfo(react)
        {
            PetNumber = petNumber,
            Name = creature.Template.Name,
            RenameAllowed = kind == SummonKind.Pet && owner is Player { Class: Class.Hunter },
        };
        if (creature.Summon.Charm.RenameAllowed)
        {
            creature.UnitFlags |= UnitFlags.PetRename | UnitFlags.PetAbandon;
        }

        // UNIT_BYTE2_FLAG_UNK3 | UNIT_BYTE2_FLAG_AURAS | UNIT_BYTE2_FLAG_UNK5 (Pet.cpp:2264)
        creature.SetByte(UpdateFields.UnitFieldBytes2, 1, 0x08 | 0x10 | 0x20);
        if (kind == SummonKind.MiniPet)
        {
            creature.UnitFlags |= UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc;
        }
    }

    /// <summary>
    /// vmangos WorldObject::GetNearPoint's primary candidate (Object.cpp:2726-2790):
    /// <c>pos + (own radius + distance + searcher radius) * (cos, sin)(angle)</c>. The collision
    /// search for a free spot around the owner (ObjectPosSelector) and the line-of-sight retry are
    /// not ported (docs/integration/pets.md).
    /// </summary>
    internal static (float X, float Y) ClosePoint(Unit owner, float summonRadius, float distance, float absoluteAngle)
    {
        float range = owner.BoundingRadius + distance + summonRadius;
        return (owner.X + (range * MathF.Cos(absoluteAngle)), owner.Y + (range * MathF.Sin(absoluteAngle)));
    }

    private bool TryGetSystems(Unit caster, uint spellId, out PetMapSystem pets, out CreatureMapSystem creatures)
    {
        pets = null!;
        creatures = null!;
        if (caster.Map is not { } map)
        {
            return false;
        }

        PetMapSystem? petSystem = map.Pets;
        CreatureMapSystem? creatureSystem = _systems(map);
        if (petSystem is null || creatureSystem is null)
        {
            Warn($"no-system:{map.MapId}", "map {Map} has no creature or pet system: summon spell {Spell} cannot spawn", map.MapId, spellId);
            return false;
        }

        pets = petSystem;
        creatures = creatureSystem;
        return true;
    }

    /// <summary>vmangos Object::SendObjectSpawnAnim: SMSG_GAMEOBJECT_SPAWN_ANIM {guid} to every player within visibility range.</summary>
    internal static void SendSpawnAnimation(Creature creature) => SendAnimation(creature, WorldOpcode.SmsgGameobjectSpawnAnim);

    /// <summary>vmangos Object::SendObjectDeSpawnAnim.</summary>
    internal static void SendDespawnAnimation(Creature creature) => SendAnimation(creature, WorldOpcode.SmsgGameobjectDespawnAnim);

    private static void SendAnimation(Creature creature, WorldOpcode opcode)
    {
        if (creature.Map is not { } map)
        {
            return;
        }

        var writer = new PacketWriter(8);
        writer.WriteUInt64(creature.Guid.Value);
        map.BroadcastInRange(creature, Map.VisibilityRange, opcode, writer.AsSpan(), includeSelf: true);
    }

    private void Warn(string key, string message, params object[] args)
    {
        if (_warned.Add(key))
        {
            _logger.LogWarning(message, args);
        }
    }
}

using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// A world creature: a <see cref="Unit"/> built from a <c>creature_template</c> row and a
/// <c>creature</c> spawn. Field initialization follows vmangos <c>Creature::InitEntry</c>,
/// <c>UpdateEntry</c> and <c>SelectLevel</c>; stats use cmangos-classic's per-level
/// health/mana columns (<c>Creature::SelectLevel</c>, "old style").
/// <para>Thread affinity: world thread (owned by its map's <see cref="CreatureMapSystem"/>).</para>
/// </summary>
public sealed partial class Creature : Unit, ICombatCreature
{
    /// <summary>OBJECT_FIELD_TYPE for creatures: TYPEMASK_OBJECT | TYPEMASK_UNIT (vmangos ObjectGuid.h).</summary>
    public const uint CreatureTypeMask = Game.TypeMask.Object | Game.TypeMask.Unit;

    /// <summary>vmangos UNIT_DISPLAY_ID_BOX: the placeholder model when a template has no display id.</summary>
    public const uint DisplayIdBox = 4;

    /// <summary>vmangos DEFAULT_NPC_RUN_SPEED_RATE.</summary>
    public const float DefaultRunSpeedRate = 1.14286f;

    /// <summary>vmangos Unit::GetCreatePowers: rage 1000, energy 100.</summary>
    public const uint CreateRage = 1000;
    public const uint CreateEnergy = 100;

    /// <summary>vmangos CREATURE_FLAG_EXTRA_NO_AGGRO / cmangos CREATURE_EXTRA_FLAG_NO_AGGRO_ON_SIGHT.</summary>
    public const uint ExtraFlagNoAggro = 0x00000002;

    private readonly Random _random;
    private CreatureTemplate _template;
    private int _templateVersion;
    private readonly Func<uint, CreatureDisplayModelMetadata?>? _displayModelResolver;
    private readonly CreatureStatRates? _statRates;

    // highGuid: HIGHGUID_PET for pets, guardians and mini pets, HIGHGUID_UNIT for everything else
    // including totems (vmangos SpellEffects.cpp; docs/integration/pets.md). guidEntry: the entry part
    // of the GUID when it is not the template entry: a pet's GUID carries its pet number there
    // (vmangos Pet::Create: Object::_Create(guidlow, petNumber, HIGHGUID_PET)).
    // statRates: the Rate.Creature.* rates (Creatures:Rates) the creature's health and damage are scaled with, at spawn and every
    // respawn; null (pets) is 1 everywhere.
    public Creature(uint counter, CreatureTemplate template, CreatureSpawn? spawn, CreatureContent content, Random random, HighGuid highGuid = HighGuid.Unit, uint guidEntry = 0, Func<uint, CreatureDisplayModelMetadata?>? displayModelResolver = null,
        CreatureStatRates? statRates = null)
        : base(ObjectGuid.WithEntry(highGuid, guidEntry != 0 ? guidEntry : template.Entry, counter), Game.TypeId.Unit, CreatureTypeMask, UpdateFields.UnitEnd)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(random);

        _template = template;
        Spawn = spawn;
        Content = content;
        _templateVersion = content.DefinitionsVersion;
        _random = random;
        _displayModelResolver = displayModelResolver;
        _statRates = statRates;

        if (spawn is not null)
        {
            MapId = spawn.MapId;
            Home = new CreatureHome(spawn.X, spawn.Y, spawn.Z, spawn.Orientation);
            MovementType = (CreatureMovementType)spawn.MovementType;
            WanderDistance = spawn.WanderDistance;
        }
        else
        {
            MovementType = (CreatureMovementType)template.MovementType;
        }

        Motion = new MotionMaster(this);
        Relocate(Home.X, Home.Y, Home.Z, Home.Orientation, 0);
        InitializeFields();
    }

    /// <summary>
    /// The creature_template row, looked up by entry in <see cref="Content"/> after every
    /// <c>.reload creature_template</c> (vmangos <c>Creature::GetCreatureInfo</c> reads the shared
    /// <c>CreatureInfo</c> table, which the reload overwrites in place). A template that no longer
    /// exists keeps the last one this creature knew. Values copied into the unit fields at creation
    /// or respawn (<see cref="InitializeFields"/>) change at the next respawn, not at the reload.
    /// </summary>
    public CreatureTemplate Template
    {
        get
        {
            if (EventTemplate is { } swapped)
            {
                return swapped; // a running game event swapped the entry (Creature.EventData.cs)
            }

            int version = Content.DefinitionsVersion;
            if (version != _templateVersion)
            {
                _template = Content.FindTemplate(_template.Entry) ?? _template;
                _templateVersion = version;
            }

            return _template;
        }
    }

    /// <summary>The spawn row, or null for creatures not placed by the database.</summary>
    public CreatureSpawn? Spawn { get; }

    public CreatureContent Content { get; }

    public uint Entry => GetUInt32(UpdateFields.ObjectFieldEntry);

    public CreatureDeathState DeathState { get; internal set; } = CreatureDeathState.Alive;

    /// <summary>First player to damage this life and that player's group at tap time (vmangos Creature.cpp:1583-1633).</summary>
    internal ObjectGuid LootTapPlayerGuid { get; set; }

    internal Group? LootTapGroup { get; set; }

    /// <summary>The map system owns death, movement and respawn even while the spawn is hidden.</summary>
    internal CreatureMapSystem? System { get; set; }

    /// <summary>The summon record of a totem, pet, guardian or mini pet; null for an ordinary creature (Game/Pets).</summary>
    internal Pets.SummonLinks? Summon { get; set; }

    /// <summary>vmangos Creature::IsTotem: a totem of the shaman lane's <see cref="Totems.TotemSystem"/> (the single totem implementation).</summary>
    public bool IsTotem => Totems.TotemQuery.IsTotem(this);

    /// <summary>vmangos Creature::IsPet: a summoned Pet object of any kind (pet, guardian, mini pet).</summary>
    public bool IsPet => Summon is { Kind: Pets.SummonKind.Pet or Pets.SummonKind.Guardian or Pets.SummonKind.MiniPet };

    /// <summary>vmangos CREATURE_STATIC_FLAG_2_NO_CRUSHING_BLOWS (CreatureDefines.h:138).</summary>
    public const uint StaticFlag2NoCrushingBlows = 0x00000010;

    /// <summary>False with CREATURE_FLAG_EXTRA_NO_PARRY (vmangos SpellCaster.cpp:565-571).</summary>
    public bool CanParry => (Template.Behaviour & CreatureBehaviourFlags.NoParry) == 0;

    /// <summary>False with CREATURE_FLAG_EXTRA_NO_BLOCK.</summary>
    public bool CanBlock => (Template.Behaviour & CreatureBehaviourFlags.NoBlock) == 0;

    /// <summary>vmangos SpellCaster.cpp:550: never a pet, nor with CREATURE_STATIC_FLAG_2_NO_CRUSHING_BLOWS.</summary>
    public bool CanCrush => !IsPet && (Template.StaticFlags2 & StaticFlag2NoCrushingBlows) == 0;

    /// <summary>False with cmangos CREATURE_EXTRA_FLAG_NO_PARRY_HASTEN: a parry does not pull this creature's swing timer forward.</summary>
    internal bool ParryHastens => (Template.Behaviour & CreatureBehaviourFlags.NoParryHasten) == 0;

    /// <summary>
    /// Whether the template lets the creature auto attack: not with vmangos CREATURE_STATIC_FLAG_NO_MELEE (CreatureAI.cpp:40)
    /// nor cmangos CREATURE_EXTRA_FLAG_NO_MELEE. The AI starts from this and scripts may still turn melee on.
    /// </summary>
    internal bool MeleeAllowedByTemplate => (Template.Behaviour & (CreatureBehaviourFlags.NoMelee | CreatureBehaviourFlags.NoMeleeFlee)) == 0;

    public bool IsWorldBoss => (CreatureRank)Template.Rank == CreatureRank.WorldBoss;

    /// <summary>
    /// vmangos Creature::_GetHealthMod of this creature (Rate.Creature.*.HP, <c>Creatures:Rates</c>): the factor on the health it spawns with
    /// (SelectLevel uses rank 0 for a pet, Creature.cpp:1796); 1 without rates.
    /// </summary>
    public float HealthRate => _statRates?.Hp(IsPet ? (uint)CreatureRank.Normal : Template.Rank) ?? 1.0f;

    /// <summary>vmangos Creature::_GetDamageMod (Rate.Creature.*.Damage): the factor on its melee and ranged weapon damage; 1 without rates.</summary>
    public float DamageRate => _statRates?.Damage(IsPet ? (uint)CreatureRank.Normal : Template.Rank) ?? 1.0f;

    /// <summary>
    /// vmangos Creature::_GetSpellDamageMod (Rate.Creature.*.SpellDamage) on the template rank, applied to the done side of its damage spells
    /// (SpellCaster.cpp:1588-1590); 1 without rates.
    /// </summary>
    public float SpellDamageRate => _statRates?.SpellDamage(Template.Rank) ?? 1.0f;

    public bool RegeneratesHealth => true;

    public uint NpcFlags
    {
        get => GetUInt32(UpdateFields.UnitNpcFlags);
        set => SetUInt32(UpdateFields.UnitNpcFlags, value);
    }

    /// <summary>Corpse time left (ms) while <see cref="DeathState"/> is <see cref="CreatureDeathState.Corpse"/>.</summary>
    internal uint CorpseDecayMs { get; set; }

    /// <summary>Map clock (ms) at which a dead creature respawns.</summary>
    internal long RespawnAtMs { get; set; }

    private bool _respawnDelayDrawn;
    private uint _respawnDelaySeconds;

    /// <summary>The respawn delay (s) of this object: the one drawn at load, else the last death's (vmangos <c>m_respawnDelay</c>); 0 before any.</summary>
    internal uint RespawnDelaySeconds => _respawnDelaySeconds;

    /// <summary>True only during the visibility pass of a runtime add (vmangos Map::Add → SetIsNewObject).</summary>
    internal bool IsNewObject { get; set; }

    /// <summary>Corpse duration by rank (vmangos Creature::Create, :1326-1343); the template's cmangos CorpseDecay overrides it only with <see cref="CreatureRespawnOptions.HonorTemplateCorpseDecay"/>.</summary>
    public uint CorpseDecaySeconds(CreatureOptions options)
    {
        if (options.Respawn.HonorTemplateCorpseDecay && Template.CorpseDecaySeconds > 0)
        {
            return Template.CorpseDecaySeconds;
        }

        return (CreatureRank)Template.Rank switch
        {
            CreatureRank.Rare => options.CorpseDecayRareSeconds,
            CreatureRank.Elite => options.CorpseDecayEliteSeconds,
            CreatureRank.RareElite => options.CorpseDecayRareEliteSeconds,
            CreatureRank.WorldBoss => options.CorpseDecayWorldBossSeconds,
            _ => options.CorpseDecayNormalSeconds,
        };
    }

    /// <summary>
    /// vmangos Creature::AllLootRemovedFromCorpse (Objects/Creature.cpp:3355-3401): a looted-out, not skinnable corpse decays sooner.
    /// The looted delay is 0 when the corpse was skinned, else <paramref name="lootedDecayRate"/> x the corpse delay, or (vmangos'
    /// default rate 0) a third of the respawn delay. While the respawn time has not passed, a respawn delay above the corpse delay always
    /// takes the looted delay, a shorter one only when it is shorter than the time left; once the respawn time has passed the
    /// corpse goes at once. A creature without a spawn row never has a respawn time to be past (ArcaneCore decision: its respawn delay is 0, so
    /// the check above would otherwise always read as past): it only shortens its corpse delay, by the looted rate or after a skin.
    /// </summary>
    internal void OnAllLootRemoved(float lootedDecayRate)
    {
        if (System is not { } system || DeathState != CreatureDeathState.Corpse)
        {
            return;
        }

        // A creature with no spawn row (GM-added, summoned) has no respawn time to be past: its delay is 0 and RespawnAtMs only the moment of death.
        // It takes the looted delay from the corpse delay alone, and keeps its corpse delay when no rate is configured.
        if (Spawn is null && !LootedForSkin && lootedDecayRate <= 0f)
        {
            return;
        }

        uint corpseDelaySeconds = CorpseDecaySeconds(system.Options);
        long lootedMs = LootedForSkin
            ? 0
            : lootedDecayRate > 0f
                ? (long)(corpseDelaySeconds * 1000.0 * lootedDecayRate)
                : _respawnDelaySeconds * 1000L / 3;
        uint looted = (uint)Math.Min(lootedMs, uint.MaxValue);

        if (Spawn is null)
        {
            if (CorpseDecayMs > looted)
            {
                CorpseDecayMs = looted;
            }
        }
        else if (RespawnAtMs >= system.ClockMs)
        {
            if (_respawnDelaySeconds > corpseDelaySeconds)
            {
                CorpseDecayMs = looted;
            }
            else if (CorpseDecayMs > looted)
            {
                CorpseDecayMs = looted;
            }
        }
        else
        {
            CorpseDecayMs = 0;
        }
    }

    /// <summary>
    /// A respawn that picks another entry of the spawn (vmangos Creature::UpdateEntry from the DEAD branch of Creature::Update, Creature.cpp:830-841;
    /// cmangos ResetEntry): the object keeps its GUID and takes the new template; <see cref="InitializeFields"/> then rewrites the unit fields.
    /// </summary>
    internal void ChangeTemplate(CreatureTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        _template = template;
        _templateVersion = Content.DefinitionsVersion;
    }

    /// <summary>
    /// Draw this object's respawn delay now (vmangos <c>m_respawnDelay = data->GetRandomRespawnTime()</c> in Creature::LoadFromDB,
    /// Creature.cpp:1963); every later death reuses it. Without a call each death draws afresh (<see cref="NextRespawnDelaySeconds"/>).
    /// </summary>
    internal void DrawRespawnDelay()
    {
        _respawnDelayDrawn = true;
        _respawnDelaySeconds = DrawFromSpawnRange();
    }

    /// <summary>
    /// The respawn delay in seconds for this death: the delay drawn at load when there is one, else a fresh
    /// urand(spawntimesecsmin, spawntimesecsmax) (vmangos CreatureData::GetRandomRespawnTime).
    /// </summary>
    public uint NextRespawnDelaySeconds()
    {
        if (Spawn is null)
        {
            return 0;
        }

        if (RespawnDelayOverrideSeconds is { } forced)
        {
            return forced;
        }

        if (!_respawnDelayDrawn)
        {
            _respawnDelaySeconds = DrawFromSpawnRange();
        }

        return _respawnDelaySeconds;
    }

    /// <summary>
    /// A respawn delay a battleground set (vmangos <c>SetRespawnDelay</c> from SpawnBGCreature: two minutes, or
    /// <see cref="RespawnNeverSeconds"/>), used instead of the spawn's own for every later death; null: the spawn's.
    /// </summary>
    public uint? RespawnDelayOverrideSeconds { get; internal set; }

    /// <summary>vmangos RESPAWN_NEVER as a delay: the creature does not come back.</summary>
    public const uint RespawnNeverSeconds = uint.MaxValue;

    private uint DrawFromSpawnRange()
    {
        if (Spawn is null)
        {
            return 0;
        }

        uint min = Math.Min(Spawn.SpawnTimeMinSeconds, Spawn.SpawnTimeMaxSeconds);
        uint max = Math.Max(Spawn.SpawnTimeMinSeconds, Spawn.SpawnTimeMaxSeconds);
        return min == max ? min : (uint)_random.NextInt64(min, (long)max + 1);
    }

    /// <summary>Walk speed in yd/s: 2.5 × speed_walk (vmangos Creature::UpdateSpeed via GetSpeedRate).</summary>
    public float CreatureWalkSpeed => BaseWalkSpeed * (Template.SpeedWalk > 0 ? Template.SpeedWalk : 1.0f);

    /// <summary>Run speed in yd/s: 7 × speed_run.</summary>
    public float CreatureRunSpeed => BaseRunSpeed * (Template.SpeedRun > 0 ? Template.SpeedRun : DefaultRunSpeedRate);

    /// <summary>
    /// (Re)initialize every template-derived field — used at creation and at respawn, as vmangos
    /// re-runs UpdateEntry/SelectLevel when a creature respawns.
    /// </summary>
    internal void InitializeFields()
    {
        CreatureTemplate t = Template;
        SetUInt32(UpdateFields.ObjectFieldEntry, t.Entry);

        // ChooseDisplayId + GetCreatureDisplayInfoRandomGender (vmangos InitEntry).
        uint displayId = EventDisplayId != 0 ? EventDisplayId : ChooseDisplayId(t, _random); // a running game event may force the model
        int selected = -1;
        for (int i = 0; i < t.DisplayIds.Count; i++)
        {
            if (t.DisplayIds[i] == displayId) { selected = i; break; }
        }
        CreatureModelInfo? model = Content.FindModel(displayId);
        if (model is { DisplayIdOtherGender: not 0 } && _random.Next(2) == 0 && Content.FindModel(model.DisplayIdOtherGender) is { } other)
        {
            model = other;
            displayId = other.DisplayId;
        }

        CreatureDisplayModelMetadata? native = _displayModelResolver?.Invoke(displayId);
        NativeScale = native?.NativeScale is > 0 and var dbcScale ? dbcScale : 1.0f;
        float templateScale = selected >= 0 && selected < t.DisplayScales.Count ? t.DisplayScales[selected] : t.Scale;
        float scale = templateScale > 0 ? templateScale : NativeScale;
        NativeScaleOverride = templateScale > 0 ? templateScale : 0;
        SetFloat(UpdateFields.ObjectFieldScaleX, scale);
        DisplayId = displayId;
        NativeDisplayId = displayId;

        // UNIT_FIELD_BYTES_0: race 0 (creatures have none), class, gender (model info), power type.
        SetByte(UpdateFields.UnitFieldBytes0, 0, 0);
        SetByte(UpdateFields.UnitFieldBytes0, 1, t.UnitClass);
        SetByte(UpdateFields.UnitFieldBytes0, 2, model?.Gender ?? 2);

        // Bounding radius / combat reach scale with the object (vmangos Unit::UpdateModelData;
        // the client model's native scale needs DBC data and is taken as 1 until it is imported).
        float normalized = scale / NativeScale;
        SetFloat(UpdateFields.UnitFieldBoundingradius, normalized * (model is { BoundingRadius: > 0 } ? model.BoundingRadius : Player.DefaultBoundingRadius));
        SetFloat(UpdateFields.UnitFieldCombatreach, normalized * (model is { CombatReach: > 0 } ? model.CombatReach : Player.DefaultCombatReach));
        if (native is { HasModelData: true })
        {
            float height = normalized * (native.CollisionHeight > 0 && native.ModelScale > 0
                ? native.CollisionHeight / native.ModelScale : 2.0f);
            if (float.IsFinite(height) && height > 0)
            {
                this.Locomotion.CollisionHeight = height;
                this.Locomotion.InvalidateEnvironmentSample();
            }
        }

        // > 1.11.2: UNIT_MOD_CAST_SPEED is a float 1.0 (vmangos InitEntry).
        SetFloat(UpdateFields.UnitModCastSpeed, 1.0f);

        // "update speed for the new CreatureInfo base speed mods": UpdateSpeed(MOVE_WALK / MOVE_RUN) at the end of
        // vmangos Creature::UpdateEntry (Creature.cpp:419-421); the speed auras change them from here on.
        WalkSpeed = CreatureWalkSpeed;
        RunSpeed = CreatureRunSpeed;

        // Sheath melee and the auras flag (vmangos UpdateEntry: SetSheath(SHEATH_STATE_MELEE),
        // UNIT_BYTES_2_OFFSET_MISC_FLAGS = UNIT_BYTE2_FLAG_AURAS 0x10).
        SetByte(UpdateFields.UnitFieldBytes2, 0, 1);
        SetByte(UpdateFields.UnitFieldBytes2, 1, 0x10);

        SetUInt32(UpdateFields.UnitFieldBaseattacktime, t.MeleeBaseAttackTime);
        SetUInt32(UpdateFields.UnitFieldBaseattacktime + 1, t.MeleeBaseAttackTime);
        SetUInt32(UpdateFields.UnitFieldRangedattacktime, t.RangedBaseAttackTime);

        FactionTemplate = t.Faction;
        NpcFlags = t.NpcFlags;

        // cmangos-classic takes UnitFlags/DynamicFlags from the template; vmangos adds PLUS_MOB
        // for any rank above normal (Creature::IsPlusMob).
        uint unitFlags = t.UnitFlags;
        if (t.Rank > (uint)CreatureRank.Normal)
        {
            unitFlags |= (uint)Game.UnitFlags.PlusMob;
        }

        SetUInt32(UpdateFields.UnitFieldFlags, unitFlags);
        SetUInt32(UpdateFields.UnitDynamicFlags, t.DynamicFlags);

        SelectLevel();
        ApplyAddon();
        ReactState = CreatureAggro.InitialReactState(t);
        SetUInt64(UpdateFields.UnitFieldTarget, 0);
    }

    /// <summary>
    /// Level = urand(min, max); health and mana interpolate between the template's min- and
    /// max-level values (cmangos-classic Creature::SelectLevel, old-style branch); the power type
    /// is mana when the creature has mana, else energy for rogues and rage otherwise with 0
    /// current power (vmangos Creature::SetInitCreaturePowerType).
    /// </summary>
    private void SelectLevel()
    {
        CreatureTemplate t = Template;
        byte min = Math.Min(t.MinLevel, t.MaxLevel);
        byte max = Math.Max(t.MinLevel, t.MaxLevel);
        byte level = min == max ? min : (byte)_random.Next(min, max + 1);
        Level = Math.Max(level, (byte)1);

        float rel = max == min ? 0f : (float)(level - min) / (max - min);
        uint minHealth = Math.Min(t.MinLevelHealth, t.MaxLevelHealth);
        uint maxHealth = Math.Max(t.MinLevelHealth, t.MaxLevelHealth);
        uint health = Math.Max(1u, minHealth + (uint)(rel * (maxHealth - minHealth)));

        // Rate.Creature.*.HP: max(1, round(healthMod * health)) for the health and the base health alike (vmangos SelectLevel, Creature.cpp:1802-1806).
        float healthRate = HealthRate;
        if (healthRate != 1.0f)
        {
            health = (uint)Math.Clamp(MathF.Round(healthRate * health), 1.0f, uint.MaxValue);
        }
        uint minMana = Math.Min(t.MinLevelMana, t.MaxLevelMana);
        uint maxMana = Math.Max(t.MinLevelMana, t.MaxLevelMana);
        uint mana = minMana + (uint)(rel * (maxMana - minMana));

        MaxHealth = health;
        Health = health;
        SetUInt32(UpdateFields.UnitFieldBaseHealth, health);
        SetUInt32(UpdateFields.UnitFieldBaseMana, mana);

        for (int i = 0; i < 5; i++)
        {
            SetUInt32(UpdateFields.UnitFieldPower1 + i, 0);
            SetUInt32(UpdateFields.UnitFieldMaxpower1 + i, 0);
        }

        PowerType power = mana > 0 ? Game.PowerType.Mana : t.UnitClass == (byte)Game.Class.Rogue ? Game.PowerType.Energy : Game.PowerType.Rage;
        SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)power);
        switch (power)
        {
            case Game.PowerType.Mana:
                SetUInt32(UpdateFields.UnitFieldMaxpower1, mana);
                SetUInt32(UpdateFields.UnitFieldPower1, mana);
                break;
            case Game.PowerType.Energy:
                SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)Game.PowerType.Energy, CreateEnergy);
                break;
            default:
                SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)Game.PowerType.Rage, CreateRage);
                break;
        }

        // Rate.Creature.*.Damage multiplies the melee and the ranged weapon damage (vmangos SelectLevel, Creature.cpp:1830-1843).
        float damageRate = DamageRate;
        SetFloat(UpdateFields.UnitFieldMindamage, t.MinMeleeDamage * damageRate);
        SetFloat(UpdateFields.UnitFieldMaxdamage, t.MaxMeleeDamage * damageRate);
        // ranged (autorepeat lane): the ranged damage and attack power fields (vmangos Creature.cpp:1840-1843 SetBaseWeaponDamage(RANGED_ATTACK)),
        // from the template exactly like the melee pair above (the importer derives them from creature_classlevelstats).
        SetFloat(UpdateFields.UnitFieldMinrangeddamage, t.MinRangedDamage * damageRate);
        SetFloat(UpdateFields.UnitFieldMaxrangeddamage, t.MaxRangedDamage * damageRate);
        SetUInt32(UpdateFields.UnitFieldRangedAttackPower, t.RangedAttackPower);
        SetUInt32(UpdateFields.UnitFieldResistances, t.Armor);
    }

    /// <summary>
    /// vmangos Creature::ChooseDisplayId (template branch): weighted by the display
    /// probabilities when any is set, otherwise equal chance among the leading non-zero ids;
    /// none at all gives the placeholder box.
    /// </summary>
    public static uint ChooseDisplayId(CreatureTemplate template, Random random)
    {
        IReadOnlyList<uint> ids = template.DisplayIds;
        IReadOnlyList<uint> weights = template.DisplayProbabilities;
        uint total = 0;
        for (int i = 0; i < ids.Count && i < weights.Count; i++)
        {
            if (ids[i] != 0)
            {
                total += weights[i];
            }
        }

        if (total > 0)
        {
            uint roll = (uint)random.NextInt64(1, (long)total + 1);
            uint sum = 0;
            for (int i = 0; i < ids.Count && i < weights.Count; i++)
            {
                if (ids[i] == 0 || weights[i] == 0)
                {
                    continue;
                }

                if (roll > sum && roll <= sum + weights[i])
                {
                    return ids[i];
                }

                sum += weights[i];
            }
        }

        int count = 0;
        while (count < ids.Count && ids[count] != 0)
        {
            count++;
        }

        return count == 0 ? DisplayIdBox : ids[random.Next(count)];
    }

}

/// <summary>A creature's home (spawn) position.</summary>
public readonly record struct CreatureHome(float X, float Y, float Z, float Orientation);

/// <summary>
/// A linear spline (vmangos linear MoveSpline): positions interpolate along the path by
/// distance over <see cref="DurationMs"/>, measured on the map clock. A single-segment spline
/// has an empty <see cref="Path"/>; a multi-point one lists every point after the start.
/// </summary>
public sealed record CreatureSpline(
    uint Id, float StartX, float StartY, float StartZ, float EndX, float EndY, float EndZ,
    bool Run, float? FinalOrientation, long StartClockMs, uint DurationMs)
{
    /// <summary>Every point after the start, the destination last, for a multi-point path; otherwise empty.</summary>
    public IReadOnlyList<Vector3> Path { get; init; } = [];

    /// <summary>How the spline ends facing.</summary>
    public SplineFacing Facing { get; init; } = SplineFacing.None;

    public bool IsFinished(long clockMs) => clockMs - StartClockMs >= DurationMs;

    public uint ElapsedMs(long clockMs) => (uint)Math.Clamp(clockMs - StartClockMs, 0, DurationMs);

    /// <summary>All points after the start (destination last).</summary>
    public IReadOnlyList<Vector3> Points => Path.Count > 0 ? Path : [new Vector3(EndX, EndY, EndZ)];

    public (float X, float Y, float Z) PositionAt(long clockMs)
    {
        float t = DurationMs == 0 ? 1f : Math.Clamp((float)(clockMs - StartClockMs) / DurationMs, 0f, 1f);
        (Vector3 position, _) = Locate(t);
        return (position.X, position.Y, position.Z);
    }

    /// <summary>The points still ahead at <paramref name="clockMs"/> (destination last).</summary>
    public IReadOnlyList<Vector3> RemainingPoints(long clockMs)
    {
        float t = DurationMs == 0 ? 1f : Math.Clamp((float)(clockMs - StartClockMs) / DurationMs, 0f, 1f);
        (_, int segment) = Locate(t);
        IReadOnlyList<Vector3> points = Points;
        return [.. points.Skip(segment)];
    }

    private (Vector3 Position, int Segment) Locate(float t)
    {
        var start = new Vector3(StartX, StartY, StartZ);
        IReadOnlyList<Vector3> points = Points;
        if (points.Count == 1)
        {
            return (Vector3.Lerp(start, points[0], t), 0);
        }

        float total = 0;
        Vector3 previous = start;
        foreach (Vector3 point in points)
        {
            total += Vector3.Distance(previous, point);
            previous = point;
        }

        float target = total * t;
        previous = start;
        for (int i = 0; i < points.Count; i++)
        {
            float segment = Vector3.Distance(previous, points[i]);
            if (target <= segment || i == points.Count - 1)
            {
                float f = segment <= 0 ? 1f : Math.Clamp(target / segment, 0f, 1f);
                return (Vector3.Lerp(previous, points[i], f), i);
            }

            target -= segment;
            previous = points[i];
        }

        return (points[^1], points.Count - 1);
    }
}

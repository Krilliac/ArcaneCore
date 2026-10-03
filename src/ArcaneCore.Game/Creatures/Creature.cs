using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
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
public sealed class Creature : Unit, ICombatCreature
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

    /// <summary>vmangos/cmangos CREATURE_FLAG_EXTRA_ALWAYS_RUN.</summary>
    public const uint ExtraFlagAlwaysRun = 0x00000040;

    /// <summary>vmangos CREATURE_FLAG_EXTRA_NO_AGGRO / cmangos CREATURE_EXTRA_FLAG_NO_AGGRO_ON_SIGHT.</summary>
    public const uint ExtraFlagNoAggro = 0x00000002;

    private readonly Random _random;

    // highGuid: HIGHGUID_PET for pets, guardians and mini pets, HIGHGUID_UNIT for everything else
    // including totems (vmangos SpellEffects.cpp; docs/integration/pets.md). guidEntry: the entry part
    // of the GUID when it is not the template entry: a pet's GUID carries its pet number there
    // (vmangos Pet::Create: Object::_Create(guidlow, petNumber, HIGHGUID_PET)).
    public Creature(uint counter, CreatureTemplate template, CreatureSpawn? spawn, CreatureContent content, Random random, HighGuid highGuid = HighGuid.Unit, uint guidEntry = 0)
        : base(ObjectGuid.WithEntry(highGuid, guidEntry != 0 ? guidEntry : template.Entry, counter), Game.TypeId.Unit, CreatureTypeMask, UpdateFields.UnitEnd)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(random);

        Template = template;
        Spawn = spawn;
        Content = content;
        _random = random;

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

    public CreatureTemplate Template { get; }

    /// <summary>The spawn row, or null for creatures not placed by the database.</summary>
    public CreatureSpawn? Spawn { get; }

    public CreatureContent Content { get; }

    public uint Entry => GetUInt32(UpdateFields.ObjectFieldEntry);

    /// <summary>Where the creature returns to (spawn point; vmangos GetRespawnCoord / home position).</summary>
    public CreatureHome Home { get; private set; }

    public CreatureMovementType MovementType { get; }

    public float WanderDistance { get; }

    public CreatureDeathState DeathState { get; internal set; } = CreatureDeathState.Alive;

    /// <summary>The map system owns death, movement and respawn even while the spawn is hidden.</summary>
    internal CreatureMapSystem? System { get; set; }

    /// <summary>
    /// vmangos Creature::IsInEvadeMode: true from EnterEvadeMode until the creature is home.
    /// Combat refuses new attacks on an evading creature (docs/integration/combat.md).
    /// </summary>
    public bool IsInEvadeMode => IsEvading;

    /// <summary>Set by the map system while the creature runs home after leaving combat.</summary>
    internal bool IsEvading { get; set; }

    /// <summary>The summon record of a totem, pet, guardian or mini pet; null for an ordinary creature (Game/Pets).</summary>
    internal Pets.SummonLinks? Summon { get; set; }

    /// <summary>vmangos Creature::IsTotem: the single source for the pets area.</summary>
    public bool IsTotem => Summon?.Kind == Pets.SummonKind.Totem;

    /// <summary>vmangos Creature::IsPet: a summoned Pet object of any kind (pet, guardian, mini pet).</summary>
    public bool IsPet => Summon is { Kind: Pets.SummonKind.Pet or Pets.SummonKind.Guardian or Pets.SummonKind.MiniPet };

    /// <summary>The script driving this creature (null outside a creature map system).</summary>
    public CreatureAI? AI { get; internal set; }

    /// <summary>The movement generator stack (vmangos MotionMaster).</summary>
    public MotionMaster Motion { get; }

    /// <summary>Where combat began (vmangos m_combatStartX/Y/Z): waypoint movers evade back here.</summary>
    internal CreatureHome? CombatStart { get; set; }

    /// <summary>The aggro hook ran for the current fight (reset by evade, death and respawn).</summary>
    internal bool HasAggroed { get; set; }

    /// <summary>The assistance call went out for the current fight (vmangos m_AlreadyCallAssistance).</summary>
    internal bool CalledAssistance { get; set; }

    public bool CanParry => true;

    public bool CanBlock => true;

    public bool CanCrush => true;

    public bool IsWorldBoss => (CreatureRank)Template.Rank == CreatureRank.WorldBoss;

    public bool RegeneratesHealth => true;

    /// <summary>vmangos CreatureAI::AttackedBy: an idle creature retaliates against its attacker.</summary>
    public void OnAttackedBy(Unit attacker)
    {
        if (AI is { } ai)
        {
            if (IsAlive && !IsEvading && Map is not null)
            {
                ai.OnAttackedBy(attacker);
            }

            return;
        }

        if (Map is not { } map || Combat.Victim is not null || !IsAlive || !map.Combat.Hooks.CanAttack(this, attacker))
        {
            return;
        }

        System?.StopMoving(this);
        map.Combat.Attack(this, attacker);
    }

    /// <summary>vmangos CreatureAI::JustDied: tell the AI, then begin the map system's corpse and respawn timers.</summary>
    public void OnJustDied(Unit? killer) => System?.OnCreatureDied(this, killer);

    public uint NpcFlags
    {
        get => GetUInt32(UpdateFields.UnitNpcFlags);
        set => SetUInt32(UpdateFields.UnitNpcFlags, value);
    }

    /// <summary>The active spline, or null when standing.</summary>
    public CreatureSpline? Spline { get; private set; }

    public bool IsMoving => Spline is not null;

    /// <summary>Corpse time left (ms) while <see cref="DeathState"/> is <see cref="CreatureDeathState.Corpse"/>.</summary>
    internal uint CorpseDecayMs { get; set; }

    /// <summary>Map clock (ms) at which a dead creature respawns.</summary>
    internal long RespawnAtMs { get; set; }

    /// <summary>True only during the visibility pass of a runtime add (vmangos Map::Add → SetIsNewObject).</summary>
    internal bool IsNewObject { get; set; }

    internal void SetHome(CreatureHome home) => Home = home;

    /// <summary>Corpse duration by rank (vmangos Creature::Create; cmangos CorpseDecay overrides when set).</summary>
    public uint CorpseDecaySeconds(CreatureOptions options)
    {
        if (Template.CorpseDecaySeconds > 0)
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

    /// <summary>A respawn delay in seconds: urand(spawntimesecsmin, spawntimesecsmax) (vmangos CreatureData::GetRandomRespawnTime).</summary>
    public uint NextRespawnDelaySeconds()
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
        uint displayId = ChooseDisplayId(t, _random);
        CreatureModelInfo? model = Content.FindModel(displayId);
        if (model is { DisplayIdOtherGender: not 0 } && _random.Next(2) == 0 && Content.FindModel(model.DisplayIdOtherGender) is { } other)
        {
            model = other;
            displayId = other.DisplayId;
        }

        float scale = t.Scale > 0 ? t.Scale : 1.0f;
        SetFloat(UpdateFields.ObjectFieldScaleX, scale);
        DisplayId = displayId;
        NativeDisplayId = displayId;

        // UNIT_FIELD_BYTES_0: race 0 (creatures have none), class, gender (model info), power type.
        SetByte(UpdateFields.UnitFieldBytes0, 0, 0);
        SetByte(UpdateFields.UnitFieldBytes0, 1, t.UnitClass);
        SetByte(UpdateFields.UnitFieldBytes0, 2, model?.Gender ?? 2);

        // Bounding radius / combat reach scale with the object (vmangos Unit::UpdateModelData;
        // the client model's native scale needs DBC data and is taken as 1 until it is imported).
        SetFloat(UpdateFields.UnitFieldBoundingradius, scale * (model is { BoundingRadius: > 0 } ? model.BoundingRadius : Player.DefaultBoundingRadius));
        SetFloat(UpdateFields.UnitFieldCombatreach, scale * (model is { CombatReach: > 0 } ? model.CombatReach : Player.DefaultCombatReach));

        // > 1.11.2: UNIT_MOD_CAST_SPEED is a float 1.0 (vmangos InitEntry).
        SetFloat(UpdateFields.UnitModCastSpeed, 1.0f);

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

        SetFloat(UpdateFields.UnitFieldMindamage, t.MinMeleeDamage);
        SetFloat(UpdateFields.UnitFieldMaxdamage, t.MaxMeleeDamage);
        SetUInt32(UpdateFields.UnitFieldResistances, t.Armor);
    }

    /// <summary>creature_addon: mount, stand state, sheath state, emote state (vmangos Creature::LoadCreatureAddon).</summary>
    private void ApplyAddon()
    {
        CreatureAddon? addon = Spawn is null ? null : Content.FindAddon(Spawn.Guid);
        SetUInt32(UpdateFields.UnitFieldMountdisplayid, addon?.MountDisplayId ?? 0);
        StandState = (StandState)(addon?.StandState ?? 0);
        if (addon is not null)
        {
            SetByte(UpdateFields.UnitFieldBytes2, 0, addon.SheathState);
        }

        SetUInt32(UpdateFields.UnitNpcEmotestate, addon?.EmoteState ?? 0);
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

    // --- movement --------------------------------------------------------------------------

    /// <summary>Start a straight move to (x, y, z); returns the spline (world thread).</summary>
    internal CreatureSpline StartSpline(float x, float y, float z, bool run, float? finalOrientation, uint splineId, long clockMs)
        => StartSpline([new Vector3(x, y, z)], run, finalOrientation is { } angle ? SplineFacing.ToAngle(angle) : SplineFacing.None, splineId, clockMs);

    /// <summary>
    /// Start a linear move through <paramref name="path"/> (every point after the current
    /// position, destination last). The duration is the path length over the walk or run speed
    /// (vmangos MoveSpline::Initialize: computeDuration over a linear spline), at least 1 ms.
    /// </summary>
    internal CreatureSpline StartSpline(IReadOnlyList<Vector3> path, bool run, SplineFacing facing, uint splineId, long clockMs)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
        {
            throw new ArgumentException("a spline needs at least one point", nameof(path));
        }

        float speed = run ? CreatureRunSpeed : CreatureWalkSpeed;
        var start = new Vector3(X, Y, Z);
        float length = 0;
        Vector3 previous = start;
        foreach (Vector3 point in path)
        {
            length += Vector3.Distance(previous, point);
            previous = point;
        }

        uint duration = Math.Max(1u, (uint)MathF.Round(length / speed * 1000f));
        Vector3 end = path[^1];
        var spline = new CreatureSpline(splineId, X, Y, Z, end.X, end.Y, end.Z, run, facing.FinalAngle, clockMs, duration)
        {
            Path = path.Count > 1 ? [.. path] : [],
            Facing = facing,
        };
        Spline = spline;

        // While on a spline the unit faces its direction of travel.
        Vector3 first = path[0];
        float dx = first.X - X;
        float dy = first.Y - Y;
        if ((dx * dx) + (dy * dy) > 0.0001f)
        {
            Orientation = NormalizeOrientation(MathF.Atan2(dy, dx));
        }

        return spline;
    }

    /// <summary>Advance along the spline; returns true when it finished this step.</summary>
    internal bool AdvanceSpline(long clockMs, uint serverTimeMs)
    {
        if (Spline is not { } spline)
        {
            return false;
        }

        (float x, float y, float z) = spline.PositionAt(clockMs);
        if (spline.IsFinished(clockMs))
        {
            Spline = null;
            Relocate(spline.EndX, spline.EndY, spline.EndZ, spline.FinalOrientation ?? Orientation, serverTimeMs);
            return true;
        }

        // Keep the movement block in step so a create block shows the live position.
        Relocate(x, y, z, Orientation, serverTimeMs);
        return false;
    }

    /// <summary>Stop where the creature is (world thread).</summary>
    internal void StopSpline(long clockMs, uint serverTimeMs)
    {
        if (Spline is { } spline)
        {
            (float x, float y, float z) = spline.PositionAt(clockMs);
            Spline = null;
            Relocate(x, y, z, Orientation, serverTimeMs);
        }
    }

    internal void ResetToHome(uint serverTimeMs)
    {
        Spline = null;
        Relocate(Home.X, Home.Y, Home.Z, Home.Orientation, serverTimeMs);
    }

    internal static float NormalizeOrientation(float o)
    {
        const float TwoPi = MathF.PI * 2f;
        o %= TwoPi;
        return o < 0 ? o + TwoPi : o;
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

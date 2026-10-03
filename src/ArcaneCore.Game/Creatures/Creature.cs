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

    private readonly Random _random;

    public Creature(uint counter, CreatureTemplate template, CreatureSpawn? spawn, CreatureContent content, Random random)
        : base(ObjectGuid.WithEntry(HighGuid.Unit, template.Entry, counter), Game.TypeId.Unit, CreatureTypeMask, UpdateFields.UnitEnd)
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

    // This creature model has no evade, pet or regeneration overrides; preserve the
    // documented CombatHooks defaults (docs/integration/combat.md). Rank is loaded content.
    public bool IsInEvadeMode => false;

    public bool CanParry => true;

    public bool CanBlock => true;

    public bool CanCrush => true;

    public bool IsWorldBoss => (CreatureRank)Template.Rank == CreatureRank.WorldBoss;

    public bool RegeneratesHealth => true;

    /// <summary>vmangos CreatureAI::AttackedBy: an idle creature retaliates against its attacker.</summary>
    public void OnAttackedBy(Unit attacker)
    {
        if (Map is not { } map || Combat.Victim is not null || !IsAlive || !map.Combat.Hooks.CanAttack(this, attacker))
        {
            return;
        }

        System?.StopMoving(this);
        map.Combat.Attack(this, attacker);
    }

    /// <summary>vmangos CreatureAI::JustDied: begin the map system's corpse and respawn timers.</summary>
    public void OnJustDied(Unit? killer) => System?.OnCreatureDied(this);

    public uint NpcFlags
    {
        get => GetUInt32(UpdateFields.UnitNpcFlags);
        set => SetUInt32(UpdateFields.UnitNpcFlags, value);
    }

    /// <summary>The active spline, or null when standing.</summary>
    public CreatureSpline? Spline { get; private set; }

    public bool IsMoving => Spline is not null;

    /// <summary>Movement generator state (owned by <see cref="CreatureMapSystem"/>).</summary>
    internal ICreatureMovementGenerator? MovementGenerator { get; set; }

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
    {
        float speed = run ? CreatureRunSpeed : CreatureWalkSpeed;
        float dx = x - X;
        float dy = y - Y;
        float dz = z - Z;
        float distance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        uint duration = Math.Max(1u, (uint)MathF.Round(distance / speed * 1000f));
        var spline = new CreatureSpline(splineId, X, Y, Z, x, y, z, run, finalOrientation, clockMs, duration);
        Spline = spline;

        // While on a spline the unit faces its direction of travel.
        if (distance > 0.01f)
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
/// A straight-line spline (vmangos linear MoveSpline with one destination): positions
/// interpolate linearly over <see cref="DurationMs"/>, measured on the map clock.
/// </summary>
public sealed record CreatureSpline(
    uint Id, float StartX, float StartY, float StartZ, float EndX, float EndY, float EndZ,
    bool Run, float? FinalOrientation, long StartClockMs, uint DurationMs)
{
    public bool IsFinished(long clockMs) => clockMs - StartClockMs >= DurationMs;

    public uint ElapsedMs(long clockMs) => (uint)Math.Clamp(clockMs - StartClockMs, 0, DurationMs);

    public (float X, float Y, float Z) PositionAt(long clockMs)
    {
        float t = DurationMs == 0 ? 1f : Math.Clamp((float)(clockMs - StartClockMs) / DurationMs, 0f, 1f);
        return (StartX + ((EndX - StartX) * t), StartY + ((EndY - StartY) * t), StartZ + ((EndZ - StartZ) * t));
    }
}

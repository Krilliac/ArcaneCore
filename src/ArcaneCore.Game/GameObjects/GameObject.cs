using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.WorldData.GameObjects;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// A game object in the world (doors, chests, herbs, mailboxes …): the GAMEOBJECT_* update
/// fields plus the use/respawn state the <see cref="GameObjectMapSystem"/> drives.
/// Re-implemented from vmangos GameObject::Create / Update (behaviour only, no code copied).
/// Thread affinity: world thread only.
/// </summary>
public sealed partial class GameObject : WorldObject
{
    internal GameObject(uint counter, GameObjectTemplate template, GameObjectSpawn? spawn)
        : base(ObjectGuid.WithEntry(HighGuid.GameObject, template.Entry, counter),
            Game.TypeId.GameObject, Game.TypeMask.Object | Game.TypeMask.GameObject, UpdateFields.GameobjectEnd)
    {
        Template = template;
        Spawn = spawn;
        if (spawn is not null)
        {
            SetPosition(spawn.X, spawn.Y, spawn.Z, spawn.Orientation);
        }

        InitializeFields();
    }

    public override ReadOnlySpan<ushort> FieldFlags => UpdateFieldTables.GameObjectVisibility;

    public override ReadOnlySpan<bool> GuidFieldStarts => UpdateFieldTables.GameObjectGuidStarts;

    /// <summary>vmangos GameObject constructor: m_updateFlag = UPDATEFLAG_ALL | UPDATEFLAG_HAS_POSITION.</summary>
    public override ObjectUpdateFlags CreateUpdateFlags => ObjectUpdateFlags.All | ObjectUpdateFlags.HasPosition;

    /// <summary>DEFAULT_WORLD_OBJECT_SIZE scaled by the template size (vmangos GameObject::GetObjectBoundingRadius).</summary>
    public override float BoundingRadius => base.BoundingRadius * (Template.Size > 0 ? Template.Size : 1.0f);

    public GameObjectTemplate Template { get; private set; }

    /// <summary>
    /// Rebind to the reloaded template of the same entry (live reload, world thread). vmangos reloads <c>gameobject_template</c> into the
    /// <c>GameObjectInfo</c> record live objects point to (ObjectMgr.cpp:8148-8153), so what is read through it from then on (data fields,
    /// loot id, lock id, ...) changes at once; the update fields filled when the object was created (display id, flags, faction) change
    /// only when it is created again.
    /// </summary>
    internal void ReplaceTemplate(GameObjectTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Entry != Template.Entry)
        {
            throw new ArgumentException("a game object keeps its entry", nameof(template));
        }

        Template = template;
    }

    /// <summary>The database spawn, or null for a runtime object (GM add, summoned chest).</summary>
    public GameObjectSpawn? Spawn { get; }

    public uint Entry => Template.Entry;

    public GameObjectType Type => (GameObjectType)Template.Type;

    public GameObjectState State
    {
        get => (GameObjectState)GetUInt32(UpdateFields.GameobjectState);
        set => SetUInt32(UpdateFields.GameobjectState, (uint)value);
    }

    public GameObjectFlags Flags
    {
        get => (GameObjectFlags)GetUInt32(UpdateFields.GameobjectFlags);
        set => SetUInt32(UpdateFields.GameobjectFlags, (uint)value);
    }

    public GameObjectLootState LootState { get; internal set; } = GameObjectLootState.Ready;

    /// <summary>Whether the object is in its map (false while despawned waiting to respawn).</summary>
    public bool IsSpawned => IsInWorld;

    /// <summary>The loot of a chest-like object while it is open, or null.</summary>
    public LootBag? Loot { get; internal set; }

    /// <summary>The player using the object (chest looter, chair sitter) or empty.</summary>
    public ObjectGuid User { get; internal set; }

    /// <summary>Respawn time on the owning system's clock (ms) while despawned.</summary>
    internal long RespawnAtMs { get; set; }

    /// <summary>
    /// vmangos m_cooldownTime of an activated door/button/goober: the whole second (system clock) at which the
    /// auto-close window ends; the object resets at the first second strictly greater than it (GameObject.cpp:572-590,
    /// <c>m_cooldownTime &lt; time(nullptr)</c>). Null = no auto-close.
    /// </summary>
    internal long? ResetAfterSecond { get; set; }

    /// <summary>vmangos m_respawnDelayTime: the respawn delay in seconds rolled between spawntimesecsmin and max when the spawn loaded.</summary>
    internal uint RolledRespawnSeconds { get; set; }

    /// <summary>
    /// A database spawn that never despawns (GameObject.cpp:985-991): it carries GO_FLAG_NODESPAWN and keeps
    /// no respawn delay. Runtime objects (summons, GM adds) never get the flag.
    /// </summary>
    public bool NeverDespawns => Spawn is { } spawn && Template.NeverDespawns(spawn.SpawnTimeSeconds);

    /// <summary>When a goober/trap may be used again (system clock, ms).</summary>
    internal long CooldownUntilMs { get; set; }

    /// <summary>Number of times a consumable object has been used since it spawned.</summary>
    internal uint UseCount { get; set; }

    /// <summary>
    /// The players who already earned a gathering skill-up from this object since it spawned (vmangos
    /// GameObject::m_SkillupSet / AddToSkillupList: one skill-up per player until the node respawns).
    /// </summary>
    public HashSet<ObjectGuid> SkillupSet { get; } = [];

    internal GameObjectMapSystem? System { get; set; }

    /// <summary>vmangos GameObject::GetRespawnDelay: the spawn's spawntimesecs magnitude (0 for a runtime object).</summary>
    public uint RespawnDelaySeconds => Spawn is null ? 0u : (uint)Math.Abs((long)Spawn.SpawnTimeSeconds);

    /// <summary>
    /// The quaternion sent in GAMEOBJECT_ROTATION: the spawn's rotation, or a pure rotation
    /// about Z derived from the orientation when the row has none (vmangos
    /// GameObject::UpdateRotationFields: z = sin(o/2), w = cos(o/2)).
    /// </summary>
    public static (float X, float Y, float Z, float W) ComputeRotation(float orientation, float r0, float r1, float r2, float r3)
    {
        if (r0 == 0 && r1 == 0 && r2 == 0 && r3 == 0)
        {
            double half = orientation / 2.0;
            return (0f, 0f, (float)Math.Sin(half), (float)Math.Cos(half));
        }

        return (r0, r1, r2, r3);
    }

    /// <summary>vmangos GameObject::Create: every GAMEOBJECT_* field from the template and spawn.</summary>
    internal void InitializeFields()
    {
        SetUInt32(UpdateFields.ObjectFieldEntry, Template.Entry);
        SetFloat(UpdateFields.ObjectFieldScaleX, Template.Size > 0 ? Template.Size : 1.0f);
        SetUInt32(UpdateFields.GameobjectDisplayid, Template.DisplayId);
        SetUInt32(UpdateFields.GameobjectFlags, Template.Flags | (NeverDespawns ? (uint)GameObjectFlags.NoDespawn : 0u));
        (float rx, float ry, float rz, float rw) = ComputeRotation(
            Orientation, Spawn?.Rotation0 ?? 0, Spawn?.Rotation1 ?? 0, Spawn?.Rotation2 ?? 0, Spawn?.Rotation3 ?? 0);
        SetFloat(UpdateFields.GameobjectRotation, rx);
        SetFloat(UpdateFields.GameobjectRotation + 1, ry);
        SetFloat(UpdateFields.GameobjectRotation + 2, rz);
        SetFloat(UpdateFields.GameobjectRotation + 3, rw);
        SetUInt32(UpdateFields.GameobjectState, Spawn?.State ?? (uint)GameObjectState.Ready);
        SetFloat(UpdateFields.GameobjectPosX, X);
        SetFloat(UpdateFields.GameobjectPosY, Y);
        SetFloat(UpdateFields.GameobjectPosZ, Z);
        SetFloat(UpdateFields.GameobjectFacing, Orientation);
        SetUInt32(UpdateFields.GameobjectDynFlags, 0);
        SetUInt32(UpdateFields.GameobjectFaction, Template.Faction);
        SetUInt32(UpdateFields.GameobjectTypeId, Template.Type);
        SetUInt32(UpdateFields.GameobjectAnimprogress, Spawn?.AnimProgress ?? 100u);
        LootState = GameObjectLootState.Ready;
        Loot = null;
        User = default;
        ResetAfterSecond = null;
        UseCount = 0;
        SkillupSet.Clear();
        ResetBehaviourState();
    }

    /// <summary>Distance between this object and <paramref name="other"/>, 3D, minus both bounding radii (vmangos GetDistance).</summary>
    public float DistanceTo(WorldObject other)
    {
        ArgumentNullException.ThrowIfNull(other);
        float dx = other.X - X;
        float dy = other.Y - Y;
        float dz = other.Z - Z;
        float d = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) - BoundingRadius - other.BoundingRadius;
        return d > 0 ? d : 0;
    }
}


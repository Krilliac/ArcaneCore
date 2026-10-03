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

    public GameObjectTemplate Template { get; }

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

    /// <summary>When an activated door/button returns to ready (system clock, ms; 0 = never).</summary>
    internal long ResetAtMs { get; set; }

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
        SetUInt32(UpdateFields.GameobjectFlags, Template.Flags);
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
        ResetAtMs = 0;
        UseCount = 0;
        SkillupSet.Clear();
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

using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.PersistentAreaAuras;

/// <summary>
/// The ground object of a persistent area aura (vmangos DynamicObject, Objects/DynamicObject.cpp): a position, a radius, the casting unit and the
/// spell effect it applies to the units standing in it. It is a networked object (TYPEID_DYNAMICOBJECT) so clients draw the spell's ground
/// visual from its fields (DYNAMICOBJECT_CASTER, _BYTES, _SPELLID, _RADIUS, _POS_X/Y/Z, _FACING; vmangos DynamicObject::Create, :76-135).
/// <para>Thread affinity: world thread (it lives in a map).</para>
/// </summary>
public sealed class DynamicObject : WorldObject
{
    /// <summary>vmangos DYNAMIC_OBJECT_AREA_SPELL (DynamicObject.h:31), the DYNAMICOBJECT_BYTES value of a persistent area aura.</summary>
    public const uint AreaSpell = 0x1;

    /// <summary>vmangos DYNAMIC_OBJECT_FARSIGHT_FOCUS (DynamicObject.h:32): the camera point of Far Sight and Eagle Eye.</summary>
    public const uint FarSightFocus = 0x2;

    /// <summary>Flare (1543): vmangos sends bytes 0x10 and the diameter as the radius ("Fix diametre visuel", DynamicObject.cpp:102-110).</summary>
    public const uint FlareSpell = 1543;

    /// <summary>A unit is refreshed at most this often (vmangos DynamicObject::NeedsRefresh, :326-330).</summary>
    public const uint RefreshIntervalMs = 2000;

    private static int s_nextCounter;

    private DynamicObject(ObjectGuid guid, Unit caster, SpellInfo spell, int effectIndex, float radius, int durationMs)
        : base(guid, Game.TypeId.DynamicObject, TypeMask.Object | TypeMask.DynamicObject, UpdateFields.DynamicobjectEnd)
    {
        Caster = caster;
        Spell = spell;
        EffectIndex = effectIndex;
        Radius = radius;
        RemainingMs = durationMs;
    }

    public override ReadOnlySpan<ushort> FieldFlags => UpdateFieldTables.DynamicObjectVisibility;

    public override ReadOnlySpan<bool> GuidFieldStarts => UpdateFieldTables.DynamicObjectGuidStarts;

    /// <summary>vmangos DynamicObject constructor: m_updateFlag = UPDATEFLAG_ALL | UPDATEFLAG_HAS_POSITION (builds above 1.8.4).</summary>
    public override ObjectUpdateFlags CreateUpdateFlags => ObjectUpdateFlags.All | ObjectUpdateFlags.HasPosition;

    /// <summary>The unit that cast the spell (vmangos GetCaster / GetUnitCaster).</summary>
    public Unit Caster { get; }

    public ObjectGuid CasterGuid => Caster.Guid;

    public SpellInfo Spell { get; }

    /// <summary>The PERSISTENT_AREA_AURA effect this object applies.</summary>
    public int EffectIndex { get; }

    /// <summary>The effect radius after the caster's SPELLMOD_RADIUS (vmangos m_radius).</summary>
    public float Radius { get; }

    /// <summary>Time left in ms (vmangos m_aliveDuration); 0 when it ran out.</summary>
    public int RemainingMs { get; internal set; }

    /// <summary>vmangos m_positive: the effect is positive (it targets friends, not enemies).</summary>
    public bool IsPositive { get; internal init; }

    /// <summary>vmangos m_channeled: a channel's object stays while the channel that owns it runs.</summary>
    public bool IsChanneled => Spell.IsChanneled;

    /// <summary>The object was deleted (vmangos m_deleted); its auras end on their next update.</summary>
    public bool IsDeleted { get; internal set; }

    /// <summary>A DYNAMIC_OBJECT_FARSIGHT_FOCUS object: the caster's camera, with no area effect.</summary>
    public bool IsFarSightFocus { get; private set; }

    /// <summary>The units this object applied its aura to, with the time since that application (vmangos m_affected).</summary>
    internal Dictionary<ObjectGuid, uint> Affected { get; } = [];

    /// <summary>vmangos DynamicObject::NeedsRefresh: a unit not yet affected, or affected more than <see cref="RefreshIntervalMs"/> ago.</summary>
    internal bool NeedsRefresh(Unit unit) => !Affected.TryGetValue(unit.Guid, out uint since) || since > RefreshIntervalMs;

    /// <summary>vmangos DynamicObject::Create (DynamicObject.cpp:76-135) for a DYNAMIC_OBJECT_AREA_SPELL object.</summary>
    internal static DynamicObject Create(Unit caster, SpellInfo spell, int effectIndex, float x, float y, float z, int durationMs, float radius, bool positive,
        uint? type = null)
    {
        uint counter = (uint)Interlocked.Increment(ref s_nextCounter) & 0x00FFFFFF;
        var dynamic = new DynamicObject(new ObjectGuid(((ulong)HighGuid.DynamicObject << 48) | counter), caster, spell, effectIndex, radius, durationMs)
        {
            IsPositive = positive,
            MapId = caster.MapId,
            X = x,
            Y = y,
            Z = z,
            Orientation = 0,
        };

        uint bytes = type ?? (spell.Id == FlareSpell ? 0x10u : AreaSpell);
        dynamic.IsFarSightFocus = bytes == FarSightFocus;
        dynamic.SetUInt32(UpdateFields.ObjectFieldEntry, spell.Id);
        dynamic.SetUInt64(UpdateFields.DynamicobjectCaster, caster.Guid.Value);
        dynamic.SetUInt32(UpdateFields.DynamicobjectBytes, bytes);
        dynamic.SetUInt32(UpdateFields.DynamicobjectSpellid, spell.Id);
        dynamic.SetFloat(UpdateFields.DynamicobjectRadius, bytes == 0x10 ? radius * 2.0f : radius);
        dynamic.SetFloat(UpdateFields.DynamicobjectPosX, x);
        dynamic.SetFloat(UpdateFields.DynamicobjectPosY, y);
        dynamic.SetFloat(UpdateFields.DynamicobjectPosZ, z);
        return dynamic;
    }
}

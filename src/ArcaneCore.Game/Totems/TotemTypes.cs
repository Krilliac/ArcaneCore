using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Totems;

/// <summary>The four totem element slots (vmangos TotemSlot, SharedDefines.h: fire, earth, water, air).</summary>
public enum TotemSlot
{
    Fire = 0,
    Earth = 1,
    Water = 2,
    Air = 3,

    /// <summary>SPELL_EFFECT_SUMMON_TOTEM (74): no slot, so no previous totem is replaced (vmangos TOTEM_SLOT_NONE).</summary>
    None = 4,
}

/// <summary>
/// Totem switches (configuration section <c>Totems</c>). Every default is the retail 1.12.1 value;
/// the non-default values are developer switches.
/// </summary>
public sealed class TotemOptions
{
    public const string SectionName = "Totems";

    /// <summary>false leaves the totem effects unregistered, so they report "not implemented" (fail closed).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Distance from the caster at which a totem is placed (vmangos Spell::EffectSummonTotem builds
    /// <c>CreatureCreatePos(caster, orientation, 2.0f, angle)</c>, SpellEffects.cpp:4952-4957).
    /// </summary>
    public float PlacementDistance { get; set; } = 2.0f;

    /// <summary>
    /// vmangos Totem::Update unsummons a totem whose owner left its visibility distance
    /// (Objects/Totem.cpp:66-76). false (developer only) lets it persist.
    /// </summary>
    public bool OwnerLeash { get; set; } = true;
}

/// <summary>One live totem: its creature, owner, slot and remaining life.</summary>
public sealed class TotemInfo
{
    internal TotemInfo(Creature creature, Unit owner, TotemSlot slot, uint summonSpellId, uint spellId, int durationMs)
    {
        Creature = creature;
        Owner = owner;
        Slot = slot;
        SummonSpellId = summonSpellId;
        SpellId = spellId;
        RemainingMs = durationMs;
    }

    public Creature Creature { get; }

    public Unit Owner { get; }

    public TotemSlot Slot { get; }

    /// <summary>The summon spell (UNIT_CREATED_BY_SPELL).</summary>
    public uint SummonSpellId { get; }

    /// <summary>The totem's own spell (<c>totem_spell</c>); 0 = a totem that only exists visually.</summary>
    public uint SpellId { get; }

    /// <summary>Milliseconds left; negative = permanent. vmangos Totem::m_duration counts down by the update diff.</summary>
    internal int RemainingMs { get; set; }

    /// <summary>Map updates still to pass before SMSG_GAMEOBJECT_SPAWN_ANIM can reach observers (they learn of the creature in the visibility phase after the next update).</summary>
    internal int SpawnAnimDelay { get; set; } = 1;

    internal bool SpawnAnimSent { get; set; }
}

/// <summary>
/// Totem lookups for other areas (stats: no dodge/block; progression and quests: no kill credit,
/// vmangos Player::IsHonorOrXPTarget excludes totems, Objects/Player.cpp:19924-19935). Keyed by the
/// creature instance, so nothing is kept alive and tests never share state.
/// </summary>
public static class TotemQuery
{
    private static readonly ConditionalWeakTable<Unit, TotemInfo> s_totems = [];

    /// <summary>Whether <paramref name="unit"/> is a summoned totem.</summary>
    public static bool IsTotem(Unit? unit) => unit is not null && s_totems.TryGetValue(unit, out _);

    /// <summary>The totem record of <paramref name="unit"/>, when it is one.</summary>
    public static bool TryGet(Unit? unit, out TotemInfo info)
    {
        if (unit is not null && s_totems.TryGetValue(unit, out TotemInfo? found))
        {
            info = found;
            return true;
        }

        info = null!;
        return false;
    }

    /// <summary>GUID of the totem's owner (UNIT_FIELD_SUMMONEDBY), or empty for a non-totem.</summary>
    public static ObjectGuid GetOwnerGuid(Unit? unit) => TryGet(unit, out TotemInfo info) ? info.Owner.Guid : default;

    internal static void Add(TotemInfo info) => s_totems.AddOrUpdate(info.Creature, info);

    internal static void Remove(TotemInfo info) => s_totems.Remove(info.Creature);
}

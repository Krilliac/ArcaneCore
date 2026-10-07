using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Totems;

/// <summary>
/// The shaman totem system: SPELL_EFFECT_SUMMON_TOTEM (74), SUMMON_TOTEM_SLOT1-4 (87-90) and
/// DESTROY_ALL_TOTEMS (110), the four element slots, and the totem lifecycle.
/// <para>
/// A totem is an ordinary <see cref="Creature"/> (the class is sealed) recorded in <see cref="TotemQuery"/>.
/// The owner relation is stored in the client-visible UNIT_FIELD_SUMMONEDBY / UNIT_FIELD_CREATEDBY /
/// UNIT_CREATED_BY_SPELL fields exactly as vmangos <c>Totem::SetOwner</c> and
/// <c>EffectSummonTotem</c> do. Ported from vmangos <c>Spells/SpellEffects.cpp:4923-5003</c> (summon),
/// <c>:5566-5573</c> (destroy all), <c>Objects/Totem.cpp:66-150</c> (update, summon, unsummon) and
/// <c>Objects/Unit.cpp:5069-5115</c> (slots).
/// </para>
/// <para>
/// Limits, recorded in docs/areas/class-shaman-paladin.md: no collision-pushed placement (vmangos
/// CreatureCreatePos uses GetFirstCollisionPosition; the point is the unclamped offset of 2 yd plus both bounding radii at the caster's Z);
/// vmangos runs the totem's own <c>Creature::Update</c> once more before unsummoning so its last aura tick is
/// not lost, here auras tick in the spell system's own update so the last tick can land up to one tick either
/// side of expiry; an unsummoned totem is removed at once instead of first dying for the client animation;
/// intrinsic per-effect immunity is applied through <see cref="TotemImmunity"/> and the spell immunity rules.
/// There is no totem bar in 1.12.1 (SMSG_TOTEM_CREATED / CMSG_TOTEM_DESTROYED exist for 2.4.3+ only), so none is sent.
/// </para>
/// </summary>
public sealed partial class TotemSystem
{
    /// <summary>MAX_TOTEM_SLOT.</summary>
    public const int SlotCount = 4;

    /// <summary>
    /// Totem passives whose aura starts with a zero periodic timer (first tick on the next update) instead of
    /// one full amplitude: vmangos Aura::CalculatePeriodic, Spells/SpellAuras.cpp:8086-8110 (the totem entries
    /// of that list: Tremor 8145, Earthbind 6474, Grounding 8179, Disease Cleansing 8172, Poison Cleansing 8167,
    /// Windfury ranks 8515, 10609, 10612). The trap and Mark entries of the same list belong to other classes.
    /// </summary>
    private static readonly HashSet<uint> s_immediateFirstTick = [8145, 6474, 8179, 8172, 8167, 8515, 10609, 10612];

    private readonly SpellSystem _spells;
    private readonly Func<Map, CreatureMapSystem?> _creatureSystems;
    private readonly Func<uint, CreatureTemplate?> _templates;
    private readonly Func<uint, uint?> _totemSpells;
    private readonly ILogger _logger;
    private readonly Dictionary<ObjectGuid, TotemInfo> _byGuid = [];
    private readonly Dictionary<ObjectGuid, TotemInfo?[]> _slotsByOwner = [];

    public TotemSystem(
        SpellSystem spells,
        Func<Map, CreatureMapSystem?> creatureSystems,
        Func<uint, CreatureTemplate?> templates,
        Func<uint, uint?> totemSpells,
        TotemOptions? options = null,
        ILogger? logger = null)
    {
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
        _creatureSystems = creatureSystems ?? throw new ArgumentNullException(nameof(creatureSystems));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _totemSpells = totemSpells ?? throw new ArgumentNullException(nameof(totemSpells));
        Options = options ?? new TotemOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    public TotemOptions Options { get; }

    /// <summary>Every live totem.</summary>
    public IReadOnlyCollection<TotemInfo> Totems => _byGuid.Values;

    /// <summary>
    /// Install the five effect handlers. Returns false (nothing registered) when <see cref="TotemOptions.Enabled"/>
    /// is off. An effect that already has a handler is an error: two owners of one effect would silently clobber
    /// each other (<see cref="SpellSystem.RegisterEffect"/> replaces by key).
    /// </summary>
    public bool Register()
    {
        if (!Options.Enabled)
        {
            return false;
        }

        SpellEffectName[] effects =
        [
            SpellEffectName.SummonTotem, SpellEffectName.SummonTotemSlot1, SpellEffectName.SummonTotemSlot2,
            SpellEffectName.SummonTotemSlot3, SpellEffectName.SummonTotemSlot4, SpellEffectName.DestroyAllTotems,
        ];
        foreach (SpellEffectName effect in effects)
        {
            if (_spells.HasEffectHandler(effect))
            {
                throw new InvalidOperationException($"Spell effect {effect} already has a handler; the totem system cannot own it too.");
            }
        }

        foreach (SpellEffectName effect in effects[..5])
        {
            _spells.RegisterEffect(effect, EffectSummonTotem);
        }

        _spells.RegisterEffect(SpellEffectName.DestroyAllTotems, EffectDestroyAllTotems);
        _spells.RegisterCastCheck(new TotemOwnerCastCheck(this));
        return true;
    }

    /// <summary>Attach the totem updater to <paramref name="map"/> once (world thread, outside the updater loop).</summary>
    public void EnsureUpdater(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.FindUpdater<TotemMapUpdater>() is null)
        {
            map.AddUpdater(new TotemMapUpdater(this));
        }
    }

    /// <summary>vmangos Unit::GetTotem: the live totem in an owner's slot.</summary>
    public Creature? GetTotem(Unit owner, TotemSlot slot)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return slot is >= TotemSlot.Fire and <= TotemSlot.Air && _slotsByOwner.TryGetValue(owner.Guid, out TotemInfo?[]? slots)
            && slots[(int)slot] is { } info && info.Creature.IsInWorld
            ? info.Creature
            : null;
    }

    /// <summary>vmangos Unit::UnsummonAllTotems.</summary>
    public void UnsummonAll(Unit owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!_slotsByOwner.TryGetValue(owner.Guid, out TotemInfo?[]? slots))
        {
            return;
        }

        foreach (TotemInfo? info in slots.ToArray())
        {
            if (info is not null)
            {
                Unsummon(info);
            }
        }
    }

    /// <summary>
    /// vmangos Totem::UnSummon: despawn animation, remove the totem's aura from itself, its owner and the owner's
    /// party members, free the slot, remove the creature.
    /// </summary>
    public void Unsummon(TotemInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!_byGuid.Remove(info.Creature.Guid))
        {
            return;
        }

        Creature totem = info.Creature;
        if (totem.IsInWorld)
        {
            Send(totem, WorldOpcode.SmsgGameobjectDespawnAnim, totem.Guid);
        }

        if (info.SpellId != 0)
        {
            _spells.RemoveAuras(totem, info.SpellId);
            _spells.RemoveAuras(info.Owner, info.SpellId);

            // "remove aura all party members too" (Totem.cpp:131-147): the owner's sub-group.
            if (info.Owner is Player)
            {
                foreach (ObjectGuid guid in _spells.Groups.GetGroupMembers(info.Owner, raid: false))
                {
                    if (guid != info.Owner.Guid && _spells.Units.Find(info.Owner, guid) is { } member)
                    {
                        _spells.RemoveAuras(member, info.SpellId);
                    }
                }
            }
        }

        if (_slotsByOwner.TryGetValue(info.Owner.Guid, out TotemInfo?[]? slots))
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (ReferenceEquals(slots[i], info))
                {
                    slots[i] = null;
                }
            }

            if (slots.All(s => s is null))
            {
                _slotsByOwner.Remove(info.Owner.Guid);
            }
        }

        TotemQuery.Remove(info);
        // An active totem may have a bolt preparing when its owner leaves or replaces it.
        // Removal must interrupt that cast now, before the spell updater can land it later.
        _spells.RemoveUnit(totem);
        if (totem.Map is { } map && _creatureSystems(map) is { } creatures)
        {
            creatures.Despawn(totem);
        }
    }

    /// <summary>The slot an effect fills (SpellEffects.cpp:4929-4943).</summary>
    internal static TotemSlot SlotOf(SpellEffectName effect) => effect switch
    {
        SpellEffectName.SummonTotemSlot1 => TotemSlot.Fire,
        SpellEffectName.SummonTotemSlot2 => TotemSlot.Earth,
        SpellEffectName.SummonTotemSlot3 => TotemSlot.Water,
        SpellEffectName.SummonTotemSlot4 => TotemSlot.Air,
        _ => TotemSlot.None,
    };

    /// <summary>
    /// Angle of a slot from the caster's orientation: <c>PI / 4 - slot * 2 * PI / 4</c> (fire pi/4, earth 7pi/4,
    /// water 5pi/4, air 3pi/4), 0 for no slot (SpellEffects.cpp:4952).
    /// </summary>
    internal static float AngleOf(TotemSlot slot)
        => slot < TotemSlot.None ? (MathF.PI / SlotCount) - ((int)slot * 2 * MathF.PI / SlotCount) : 0f;

    /// <summary>
    /// CreatureCreatePos::SelectFinalPoint (Creature.cpp:232) calls <c>caster.GetClosePoint(..., totem radius, distance, angle)</c>:
    /// GetNearPointAroundPosition (Object.cpp:2748) passes <c>distance + totem radius</c> to GetNearPoint2DAroundPosition,
    /// which adds the caster's own bounding radius again (Object.cpp:2728-2729). The reach is therefore
    /// <c>distance + caster radius + totem radius</c>.
    /// </summary>
    internal static (float X, float Y) PlacementPoint(Unit caster, float totemRadius, float distance, float angle)
    {
        float reach = distance + totemRadius + caster.BoundingRadius;
        return (caster.X + (reach * MathF.Cos(angle)), caster.Y + (reach * MathF.Sin(angle)));
    }

    // vmangos Spell::EffectSummonTotem.
    private void EffectSummonTotem(SpellEffectContext context)
    {
        Unit caster = context.Caster;
        if (!ReferenceEquals(context.Target, caster) || caster.Map is not { } map)
        {
            return;
        }

        TotemSlot slot = SlotOf(context.Effect.Effect);
        if (slot != TotemSlot.None && GetTotem(caster, slot) is { } old && _byGuid.TryGetValue(old.Guid, out TotemInfo? oldInfo))
        {
            Unsummon(oldInfo);
        }

        uint entry = (uint)context.Effect.MiscValue;
        if (_templates(entry) is not { } template)
        {
            _logger.LogWarning("Creature entry {Entry} does not exist but is used in spell {Spell} totem summon", entry, context.Spell.Id);
            return;
        }

        if (_creatureSystems(map) is not { } creatures)
        {
            _logger.LogWarning("Map {Map} has no creature system; totem of spell {Spell} not summoned", map.MapId, context.Spell.Id);
            return;
        }

        // The totem is driven by this system, never by creature AI: NullAI (no aggro, no wandering), idle movement.
        // Its model (hence bounding radius) is chosen when it is created, so the final point is set right after.
        float angle = caster.Orientation + AngleOf(slot);
        float provisional = Options.PlacementDistance + caster.BoundingRadius;
        Creature totem = creatures.SpawnTemporary(
            template with { AIName = "NullAI", MovementType = 0 },
            caster.X + (provisional * MathF.Cos(angle)), caster.Y + (provisional * MathF.Sin(angle)), caster.Z, caster.Orientation);
        (float x, float y) = PlacementPoint(caster, totem.BoundingRadius, Options.PlacementDistance, angle);
        totem.SetPosition(x, y, caster.Z, caster.Orientation);
        totem.SetHome(new CreatureHome(x, y, caster.Z, caster.Orientation));
        totem.AddMovementFlags(MovementFlags.Root);

        // Totem::SetOwner and the rest of EffectSummonTotem.
        totem.SetUInt64(UpdateFields.UnitFieldCreatedby, caster.Guid.Value);
        totem.SetUInt64(UpdateFields.UnitFieldSummonedby, caster.Guid.Value);
        totem.FactionTemplate = caster.FactionTemplate;
        totem.Level = caster.Level;
        if (context.Value > 0)
        {
            totem.MaxHealth = (uint)context.Value;
            totem.Health = (uint)context.Value;
        }

        totem.SetUInt32(UpdateFields.UnitCreatedBySpell, context.Spell.Id);
        UnitFlags flags = totem.UnitFlags;
        if (caster is Player)
        {
            flags |= UnitFlags.PlayerControlled;
        }

        if ((caster.UnitFlags & UnitFlags.Pvp) != 0)
        {
            flags |= UnitFlags.Pvp;
        }

        totem.UnitFlags = flags;

        uint spellId = _totemSpells(entry) ?? 0;
        var info = new TotemInfo(totem, caster, slot, context.Spell.Id, spellId, context.Spell.GetDuration());
        _byGuid[totem.Guid] = info;
        TotemQuery.Add(info);
        if (slot != TotemSlot.None)
        {
            if (!_slotsByOwner.TryGetValue(caster.Guid, out TotemInfo?[]? slots))
            {
                _slotsByOwner[caster.Guid] = slots = new TotemInfo?[SlotCount];
            }

            slots[(int)slot] = info;
        }

        if (map.FindUpdater<TotemMapUpdater>() is null)
        {
            // A summon from inside the updater loop cannot add an updater; do it right after the update.
            map.RunAfterUpdate(() => EnsureUpdater(map));
        }

        // Totem::Summon: a totem whose spell has a cast time is an active totem and casts it itself; a passive one
        // casts it on itself, triggered.
        if (spellId != 0 && _spells.Store.Get(spellId) is { } totemSpell && totemSpell.CastTime.Base <= 0)
        {
            _spells.CastSpell(totem, spellId, SpellCastTargets.ForSelf(), triggered: true);
            ApplyImmediateFirstTick(totem, spellId);
        }
    }

    // SpellAuras.cpp:8053-8112: periodic timer left at 0 for the listed totem passives and Stoneclaw (no visual, icon 689).
    private void ApplyImmediateFirstTick(Creature totem, uint spellId)
    {
        foreach (SpellAuraHolder holder in _spells.GetAuras(totem).Where(h => h.Spell.Id == spellId))
        {
            bool immediate = s_immediateFirstTick.Contains(spellId) || (holder.Spell.SpellVisual == 0 && holder.Spell.SpellIconId == 689);
            if (!immediate)
            {
                continue;
            }

            foreach (SpellAura aura in holder.Auras.OfType<SpellAura>().Where(a => a.IsPeriodic))
            {
                aura.PeriodicTimer = 0;
            }
        }
    }

    private void EffectDestroyAllTotems(SpellEffectContext context)
    {
        if (ReferenceEquals(context.Target, context.Caster))
        {
            UnsummonAll(context.Caster);
        }
    }

    /// <summary>vmangos Totem::Update for every totem of <paramref name="map"/> (order of summoning).</summary>
    internal void Update(Map map, uint diffMs)
    {
        foreach (TotemInfo info in _byGuid.Values.Where(t => ReferenceEquals(t.Creature.Map, map)).ToArray())
        {
            if (!_byGuid.ContainsKey(info.Creature.Guid))
            {
                continue;
            }

            if (!IsOwnerValid(info, map) || !info.Creature.IsAlive)
            {
                Unsummon(info);
                continue;
            }

            if (info.RemainingMs >= 0)
            {
                // "if (m_duration <= update_diff) UnSummon(); else m_duration -= update_diff;"
                if (info.RemainingMs <= diffMs)
                {
                    Unsummon(info);
                    continue;
                }

                info.RemainingMs -= (int)diffMs;
            }

            if (!info.SpawnAnimSent)
            {
                if (info.SpawnAnimDelay > 0)
                {
                    info.SpawnAnimDelay--;
                }
                else
                {
                    info.SpawnAnimSent = true;
                    Send(info.Creature, WorldOpcode.SmsgGameobjectSpawnAnim, info.Creature.Guid);
                }
            }

            UpdateActiveTotem(info, map);
        }
    }

    /// <summary>The owner left the map (logout, far teleport): vmangos Player::RemoveFromWorld unsummons its totems (Player.cpp:2208-2216).</summary>
    internal void OnOwnerRemoved(Player player) => UnsummonAll(player);

    private static void Send(Creature totem, WorldOpcode opcode, ObjectGuid guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid.Value);
        totem.Map?.BroadcastToObservers(totem, opcode, writer.ToArray());
    }
}

/// <summary>The per-map hook that runs <see cref="TotemSystem.Update"/> (after the creature system, which attaches first).</summary>
internal sealed class TotemMapUpdater(TotemSystem system) : IMapUpdater
{
    public void Update(Map map, uint diffMs) => system.Update(map, diffMs);

    public void OnPlayerRemoved(Map map, Player player) => system.OnOwnerRemoved(player);
}

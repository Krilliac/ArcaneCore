using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells.PersistentAreaAuras;
using ArcaneCore.Game.Spells.Rules.Immunity;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_PERSISTENT_AREA_AURA (27): Consecration, Blizzard, Rain of Fire, Hurricane, Volley, Death and Decay, Flamestrike's burn. The
/// effect puts a <see cref="DynamicObject"/> on the ground; every update that object gives its spell's aura to the units in its radius and the
/// aura leaves a unit that walks out (or when the object goes).
/// <list type="bullet">
/// <item>Creation (vmangos Spell::EffectPersistentAA, SpellEffects.cpp:1989-2017, run once per cast "on the ground" after the unit targets,
/// Spell.cpp:3971-3976): the effect radius through the caster's SPELLMOD_RADIUS, the cast's duration, at the destination (a unit target's position
/// when the spell has one, Spell.cpp:3173-3176; the caster's otherwise). The unit targets the selectors list for such an effect appear in
/// SMSG_SPELL_GO but the effect never runs on them (Spell.cpp:1129-1137). A channel whose first effect is the persistent aura makes the object its
/// channel object (Spell.cpp:4830-4833).</item>
/// <item>Update (DynamicObject::Update, DynamicObject.cpp:161-227): the object goes when its caster left the world or the caster's map, and when its
/// time ran out, unless it is the running channel's object. Each update it visits the units within its radius (DynamicObjectUpdater::VisitHelper,
/// GridNotifiersImpl.h:123-262): alive, not a GM, a valid attack target for a negative effect (a valid helper target for a positive one), in line of
/// sight of the object for a player caster, not refreshed within 2 s, the patch 1.7 rule (a non-PvP-flagged player's negative area does not hit
/// players outside a duel, unless both are free-for-all PvP), combat for a negative effect without NO_THREAT / THREAT_ONLY_ON_MISS / NO_INITIAL_THREAT / NOT_AN_ACTION, and not
/// immune. An existing holder of the spell from the caster has its duration raised to the object's; otherwise a new holder with the spell's
/// duration is added (a channel's holder takes the channel's remaining time).</item>
/// <item>The aura's own update (PersistentAreaAura::Update, SpellAuras.cpp:892-919): a unit outside the radius, or whose object is gone, loses it
/// (unless the spell has SPELL_ATTR_EX3_NO_AVOIDANCE), and may get it again when it returns.</item>
/// <item>A channel that ends or is interrupted removes its objects (Spell::cancel and Spell::finish, Spell.cpp:3595 and :4794).</item>
/// </list>
/// LIMITS: the object never moves; a game object's cast (hunter traps) owns the object through the casting unit; the 2 s refresh window starts
/// at the object's first visit, as in vmangos; an existing holder that lacks the effect's aura is not given it (vmangos adds the aura to it).
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>SPELL_ATTR_EX_THREAT_ONLY_ON_MISS (SpellDefines.h:891).</summary>
    private const uint AttributeExThreatOnlyOnMiss = 0x00200000;

    /// <summary>SPELL_ATTR_EX2_NOT_AN_ACTION (SpellDefines.h:934).</summary>
    private const uint AttributeEx2NotAnAction = 0x10000000;

    /// <summary>SPELL_ATTR_EX3_NO_AVOIDANCE (SpellDefines.h:950): "Persistent Area Aura not removed on leaving radius".</summary>
    private const uint AttributeEx3NoAvoidance = 0x00000040;

    private readonly List<DynamicObject> _dynamicObjects = [];
    private readonly Dictionary<SpellAuraHolder, DynamicObject> _persistentHolders = new(ReferenceEqualityComparer.Instance);

    /// <summary>The persistent area aura objects in the world.</summary>
    public IReadOnlyList<DynamicObject> DynamicObjects => _dynamicObjects;

    /// <summary>The objects <paramref name="caster"/> put on the ground (vmangos SpellCaster::m_spellDynObjects).</summary>
    public IEnumerable<DynamicObject> GetDynamicObjects(Unit caster) => _dynamicObjects.Where(d => ReferenceEquals(d.Caster, caster));

    /// <summary>The object a persistent area aura holder came from, or null for any other holder.</summary>
    public DynamicObject? FindDynamicObject(SpellAuraHolder holder) => _persistentHolders.GetValueOrDefault(holder);

    /// <summary>vmangos Spell::DoAllEffectOnTarget (Spell.cpp:1129-1137): the effects of <paramref name="mask"/> without the persistent area auras.</summary>
    internal static int WithoutGroundEffects(SpellInfo spell, int mask)
    {
        for (int i = 0; i < spell.Effects.Count && i < SpellConstants.MaxEffects; i++)
        {
            if (spell.Effects[i].Effect == SpellEffectName.PersistentAreaAura)
            {
                mask &= ~(1 << i);
            }
        }

        return mask;
    }

    /// <summary>vmangos Spell::_handle_immediate_phase "process ground" (Spell.cpp:3971-3976) → Spell::EffectPersistentAA once per effect.</summary>
    internal void HandleGroundEffects(SpellCast cast, Unit? unitTarget)
    {
        SpellInfo spell = cast.Spell;
        Unit caster = cast.Caster;
        for (int i = 0; i < spell.Effects.Count && i < SpellConstants.MaxEffects; i++)
        {
            SpellEffectInfo effect = spell.Effects[i];
            if (effect.Effect != SpellEffectName.PersistentAreaAura || caster.Map is not { } map || IsQuestSettlementPending(caster))
            {
                continue;
            }

            float radius = effect.Radius > 0 ? ModFloat(caster, spell, SpellModOp.Radius, effect.Radius) : effect.Radius;
            (float x, float y, float z) = cast.Targets.HasDest ? cast.Targets.Dest
                : unitTarget is not null && !ReferenceEquals(unitTarget, caster) ? (unitTarget.X, unitTarget.Y, unitTarget.Z)
                : (caster.X, caster.Y, caster.Z);
            int duration = cast.State == SpellCastState.Casting ? cast.Timer : cast.Duration;
            var dynamic = DynamicObject.Create(caster, spell, i, x, y, z, duration, radius, spell.IsPositiveEffect(i, Store.Get));
            map.AddObject(dynamic, active: false, isNewObject: true);
            _dynamicObjects.Add(dynamic);
            if (i == 0 && spell.IsChanneled && cast.State == SpellCastState.Casting)
            {
                caster.SetUInt64(UpdateFields.UnitFieldChannelObject, dynamic.Guid.Value);
            }
        }
    }

    /// <summary>Remove the objects of <paramref name="spellId"/> (0 = all) that <paramref name="caster"/> put down (vmangos SpellCaster::RemoveDynObject).</summary>
    public void RemoveDynamicObjects(Unit caster, uint spellId = 0)
    {
        ArgumentNullException.ThrowIfNull(caster);
        foreach (DynamicObject dynamic in _dynamicObjects.Where(d => ReferenceEquals(d.Caster, caster) && (spellId == 0 || d.Spell.Id == spellId)).ToArray())
        {
            DeleteDynamicObject(dynamic);
        }
    }

    /// <summary>vmangos DynamicObject::Delete: the despawn animation, then out of the map; its auras end on their next update.</summary>
    private void DeleteDynamicObject(DynamicObject dynamic)
    {
        if (dynamic.IsDeleted)
        {
            return;
        }

        dynamic.IsDeleted = true;
        _dynamicObjects.Remove(dynamic);
        if (dynamic.Map is { } map)
        {
            var writer = new Protocol.PacketWriter(8);
            writer.WriteUInt64(dynamic.Guid.Value);
            map.BroadcastToObservers(dynamic, Protocol.WorldOpcode.SmsgGameobjectDespawnAnim, writer.ToArray());
            map.RemoveObject(dynamic);
        }
    }

    /// <summary>Every persistent area aura object, then every persistent area aura holder (once per <see cref="Update"/>).</summary>
    private void UpdateDynamicObjects(uint diffMs)
    {
        if (_dynamicObjects.Count > 0)
        {
            foreach (DynamicObject dynamic in _dynamicObjects.ToArray())
            {
                UpdateDynamicObject(dynamic, diffMs);
            }
        }

        if (_persistentHolders.Count > 0)
        {
            UpdatePersistentHolders();
        }
    }

    private void UpdateDynamicObject(DynamicObject dynamic, uint diffMs)
    {
        Unit caster = dynamic.Caster;
        if (dynamic.Map is not { } map || !caster.IsInWorld || !ReferenceEquals(caster.Map, map))
        {
            DeleteDynamicObject(dynamic);
            return;
        }

        dynamic.RemainingMs = Math.Max(0, dynamic.RemainingMs - (int)diffMs);
        bool delete = dynamic.RemainingMs == 0
            && (!dynamic.IsChanneled || caster.GetUInt64(UpdateFields.UnitFieldChannelObject) != dynamic.Guid.Value);
        foreach (ObjectGuid guid in dynamic.Affected.Keys.ToArray())
        {
            dynamic.Affected[guid] += diffMs;
        }

        if (dynamic.Radius > 0 && !IsQuestSettlementPending(caster))
        {
            foreach (Unit unit in UnitsInRadius(map, dynamic.X, dynamic.Y, dynamic.Z, dynamic.Radius + dynamic.BoundingRadius))
            {
                VisitPersistentAreaAura(dynamic, caster, map, unit);
            }
        }

        if (delete)
        {
            DeleteDynamicObject(dynamic);
        }
    }

    /// <summary>vmangos DynamicObjectUpdater::VisitHelper (GridNotifiersImpl.h:123-262).</summary>
    private void VisitPersistentAreaAura(DynamicObject dynamic, Unit caster, Map map, Unit target)
    {
        SpellInfo spell = dynamic.Spell;
        int index = dynamic.EffectIndex;
        if (!target.IsAlive || IsQuestSettlementPending(target)
            || (target is Player { IsGameMaster: true } && !ReferenceEquals(target, caster)))
        {
            return;
        }

        if (!dynamic.IsPositive ? !Relations.IsHostile(caster, target) : !Relations.CanAssist(caster, target))
        {
            return;
        }

        // Must check LoS with the target to prevent casting through objects by targeting the floor. Let creatures cheat.
        if (caster is Player && !IsInLineOfSightFromPoint(spell, map, dynamic.X, dynamic.Y, dynamic.Z, target))
        {
            return;
        }

        if (!dynamic.NeedsRefresh(target))
        {
            return;
        }

        // Patch 1.7.0: "Consecration and other similar spells can no longer be used by non-PvP flagged players to damage PvP flagged enemies."
        // Two players who are both free-for-all PvP (an arena, a free-for-all realm) are spared the rule (GridNotifiersImpl.h:170).
        if (!dynamic.IsPositive && caster.GetCharmerOrOwnerPlayerOrSelf() is { } attacker && target.GetCharmerOrOwnerPlayerOrSelf() is { } attacked
            && (attacker.UnitFlags & UnitFlags.Pvp) == 0 && !((attacker.Flags & PlayerFlags.FfaPvp) != 0 && (attacked.Flags & PlayerFlags.FfaPvp) != 0)
            && !DuelRules.IsInDuelWith(attacker, attacked))
        {
            return;
        }

        if (!dynamic.IsPositive
            && ((uint)spell.AttributesEx & (MapCombat.AttributeExNoThreat | AttributeExThreatOnlyOnMiss)) == 0
            && ((uint)spell.AttributesEx2 & (AttributeEx2NoInitialThreat | AttributeEx2NotAnAction)) == 0)
        {
            // Enter combat (AttackedBy, AddThreat, SetInCombatWith both ways): a harmless spell hit does all of it.
            Damage.DealSpellDamage(caster, target, spell, 0, periodic: false, startsCombat: StartsCombat(caster, target));
        }

        if (ImmunityRules.IsImmuneToSpell(this, target, spell, castOnSelf: false)
            || ImmunityRules.IsImmuneToSpellEffect(this, target, spell, index, castOnSelf: false))
        {
            return;
        }

        // "in case 2 dynobject overlap areas for same spell, same holder is selected, so dynobjects share holder"
        SpellAuraHolder? existing = GetAuras(target).FirstOrDefault(h => !h.IsRemoved && h.Spell.Id == spell.Id && h.CasterGuid == caster.Guid);
        if (existing is not null)
        {
            if (existing.Auras[index] is not null && !dynamic.IsChanneled && !existing.IsPermanent && existing.Duration < dynamic.RemainingMs)
            {
                existing.Duration = dynamic.RemainingMs;
                SendAuraDuration(existing);
            }

            _persistentHolders.TryAdd(existing, dynamic);
        }
        else if (BuildPersistentHolder(dynamic, caster, target) is { } holder)
        {
            AddAuraHolder(holder);
            if (!holder.IsRemoved && GetAuras(target).Contains(holder))
            {
                _persistentHolders[holder] = dynamic;
            }
        }

        dynamic.Affected[target.Guid] = 0;
    }

    /// <summary>
    /// vmangos CreateSpellAuraHolder + PersistentAreaAura: the spell's duration (a channel's remaining time), the effect value rolled for the caster
    /// through the value modifiers and, for a periodic damage or heal effect, the caster side of its bonus (Aura::HandlePeriodicDamage on apply).
    /// </summary>
    private SpellAuraHolder? BuildPersistentHolder(DynamicObject dynamic, Unit caster, Unit target)
    {
        SpellInfo spell = dynamic.Spell;
        int index = dynamic.EffectIndex;
        SpellEffectInfo effect = spell.Effects[index];
        if (!AuraHandlers.ContainsKey(effect.AuraType))
        {
            ReportUnsupported("aura", (uint)effect.AuraType, spell.Id);
        }

        int duration = dynamic.IsChanneled && GetState(caster.Guid)?.CurrentCast is { State: SpellCastState.Casting } channel && channel.Spell.Id == spell.Id
            ? channel.Timer
            : DurationFor(caster, spell);
        var holder = new SpellAuraHolder(spell, target, caster, _auraCasterOwners.GetValue(caster, static c => new AuraCasterOwner(c)), duration);
        int value = ModInt(caster, spell, SpellModOp.AllEffects,
            ModifyValue(SpellValueKind.EffectValue, caster, spell, index, spell.CalculateEffectValue(index, caster.Level, Random), target));
        SpellAmountStage? stage = effect.AuraType switch
        {
            AuraType.PeriodicDamage or AuraType.PeriodicLeech => SpellAmountStage.DamageOverTimeSnapshot,
            AuraType.PeriodicHeal => SpellAmountStage.HealOverTimeSnapshot,
            _ => null,
        };
        int amount = AmountModifier is null || stage is null ? value : (int)AmountModifier.Modify(stage.Value, caster, target, spell, index, value, 1);
        var aura = new SpellAura(index, effect.AuraType, amount, ModifiedAmplitude(caster, spell, effect), effect.MiscValue, target.PowerType);
        aura.PeriodicTimer = PeriodicTiming.InitialTimer(spell, aura);
        holder.SetAura(aura);
        return holder.IsEmpty ? null : holder;
    }

    /// <summary>vmangos PersistentAreaAura::Update (SpellAuras.cpp:892-919) for every holder a ground object gave.</summary>
    private void UpdatePersistentHolders()
    {
        foreach ((SpellAuraHolder holder, DynamicObject dynamic) in _persistentHolders.ToArray())
        {
            if (holder.IsRemoved)
            {
                _persistentHolders.Remove(holder);
                continue;
            }

            if (((uint)holder.Spell.AttributesEx3 & AttributeEx3NoAvoidance) != 0)
            {
                continue; // Explosive Trap Effect is not removed on leaving the radius
            }

            Unit target = holder.Target;
            if (!dynamic.IsDeleted && ReferenceEquals(dynamic.Map, target.Map) && WithinObjectRadius(dynamic, target))
            {
                continue;
            }

            dynamic.Affected.Remove(target.Guid); // let a later visit apply it again when the unit returns
            _persistentHolders.Remove(holder);
            if (!IsQuestSettlementPending(target) && GetState(target.Guid) is { } state && ReferenceEquals(state.Unit, target))
            {
                RemoveHolder(state, holder, AuraRemoveMode.Default);
            }
        }
    }

    /// <summary>vmangos <c>IsWithinDistInMap(dynObject, radius)</c>: the 3D distance less both bounding radii.</summary>
    private static bool WithinObjectRadius(DynamicObject dynamic, Unit unit)
    {
        float dx = unit.X - dynamic.X;
        float dy = unit.Y - dynamic.Y;
        float dz = unit.Z - dynamic.Z;
        float reach = dynamic.Radius + dynamic.BoundingRadius + unit.BoundingRadius;
        return (dx * dx) + (dy * dy) + (dz * dz) <= reach * reach;
    }
}

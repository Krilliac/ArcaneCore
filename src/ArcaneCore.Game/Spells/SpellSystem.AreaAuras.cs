using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>vmangos SPELL_ATTR_EX3_ONLY_ON_PLAYER (AttributesEx3 bit 8).</summary>
    private const uint AreaAuraOnlyOnPlayer = 0x00000100;

    /// <summary>
    /// SPELL_EFFECT_APPLY_AREA_AURA_PARTY (vmangos Spell::EffectApplyAreaAura): the caster gets the
    /// aura as its source; <see cref="UpdateAreaAuras"/> spreads it to party members in range.
    /// </summary>
    private void EffectApplyAreaAuraParty(SpellEffectContext context)
    {
        if (!ReferenceEquals(context.Target, context.Caster))
        {
            // vmangos applies area auras to the caster only; members get theirs from the source.
            return;
        }

        EffectApplyAura(context);
    }

    /// <summary>
    /// SPELL_EFFECT_APPLY_AREA_AURA_FRIEND (vmangos Spell::EffectApplyAreaAura, AreaAura constructor SpellAuras.cpp:438-440): the caster holds the
    /// source with its real aura (unlike the enemy form), and <see cref="UpdateAreaAuras"/> puts the aura on every living unit within the effect radius
    /// that is friendly to the caster and takes it away from those that leave the radius, die or stop being friendly.
    /// </summary>
    private void EffectApplyAreaAuraFriend(SpellEffectContext context)
    {
        if (!ReferenceEquals(context.Target, context.Caster))
        {
            return;
        }

        EffectApplyAura(context);
    }

    /// <summary>
    /// SPELL_EFFECT_APPLY_AREA_AURA_ENEMY (vmangos Spell::EffectApplyAreaAura, AreaAura constructor SpellAuras.cpp:441-445): like the party effect, the
    /// caster alone holds the source, and its aura does nothing on itself (type NONE, which is why it also takes no period). <see cref="UpdateAreaAuras"/>
    /// puts the real aura on every enemy within the effect radius and takes it away from those that leave it.
    /// </summary>
    private void EffectApplyAreaAuraEnemy(SpellEffectContext context)
    {
        if (!ReferenceEquals(context.Target, context.Caster))
        {
            return;
        }

        EffectApplyAura(context);
        if (context.PendingHolder?.Auras[context.EffectIndex] is { } real)
        {
            context.PendingHolder.SetAura(new SpellAura(real.EffectIndex, AuraType.None, real.Amount, 0, real.MiscValue, context.Target.PowerType));
        }
    }

    // Scratch collections for the per-area-source, per-tick update. Each has an in-use flag: AddAuraHolder/RemoveHolder can recurse into
    // another area-aura update, and the outer frame is still iterating its scratch, so a nested acquire gets a fresh local instead.
    // World thread only. Always cleared on release so no references are retained.
    private readonly List<SpellAuraHolder> _areaSourceScratch = [];
    private bool _areaSourceScratchInUse;
    private readonly List<ObjectGuid> _areaMemberScratch = [];
    private bool _areaMemberScratchInUse;
    private readonly HashSet<ObjectGuid> _areaInRangeScratch = [];
    private bool _areaInRangeScratchInUse;
    private readonly List<KeyValuePair<ObjectGuid, SpellAuraHolder>> _areaChildScratch = [];
    private bool _areaChildScratchInUse;

    /// <summary>Test seam: one <see cref="UpdateAreaAuras"/> pass for <paramref name="unit"/>'s state without the rest of <see cref="Update"/> (allocation tests).</summary>
    internal void UpdateAreaAurasOf(Unit unit)
    {
        if (GetState(unit.Guid) is { } state)
        {
            UpdateAreaAuras(state);
        }
    }

    /// <summary>
    /// vmangos AreaAura::Update (party branch, re-implemented): each update, an area aura source on
    /// <paramref name="state"/>'s unit puts a child holder (same caster ownership token, the
    /// source's remaining duration) on every alive party member of the same map within the effect
    /// radius, and removes children from members that left the party or the radius. A member that
    /// already holds the spell from another source is skipped. Children are never saved.
    /// </summary>
    private void UpdateAreaAuras(UnitSpellState state)
    {
        if (state.Auras.Count == 0 || state.Unit.Map is not { } map)
        {
            return;
        }

        // Most units hold no area aura source: look before taking the snapshot (and the per-source closures) at all.
        bool anySource = false;
        foreach (SpellAuraHolder holder in state.Auras)
        {
            if (!holder.IsRemoved && holder.IsAreaSource)
            {
                anySource = true;
                break;
            }
        }

        if (!anySource)
        {
            return;
        }

        // Snapshot of the holders (the update can add or remove holders on this unit), without a per-call array.
        bool owned = !_areaSourceScratchInUse;
        List<SpellAuraHolder> sources = owned ? _areaSourceScratch : new List<SpellAuraHolder>(state.Auras.Count);
        _areaSourceScratchInUse |= owned;
        try
        {
            sources.AddRange(state.Auras);
            for (int i = 0; i < sources.Count; i++)
            {
                SpellAuraHolder source = sources[i];
                if (source.IsRemoved || !source.IsAreaSource)
                {
                    continue;
                }

                UpdateAreaAuraSource(state, source, map);
            }
        }
        finally
        {
            if (owned)
            {
                sources.Clear();
                _areaSourceScratchInUse = false;
            }
        }
    }

    private void UpdateAreaAuraSource(UnitSpellState state, SpellAuraHolder source, Maps.Map map)
    {
        // Max radius over the area effects (0 when none; a NaN is passed over as Enumerable.Max does), as a plain loop.
        float radius = 0;
        bool anyRadius = false;
        IReadOnlyList<SpellEffectInfo> effects = source.Spell.Effects;
        for (int i = 0; i < effects.Count; i++)
        {
            if (effects[i].Effect is SpellEffectName.ApplyAreaAuraParty or SpellEffectName.ApplyAreaAuraFriend or SpellEffectName.ApplyAreaAuraEnemy && (!anyRadius || effects[i].Radius > radius || float.IsNaN(radius)))
            {
                radius = effects[i].Radius;
                anyRadius = true;
            }
        }

        // vmangos AreaAura::AreaAura (SpellAuras.cpp:420-422): SPELLMOD_RADIUS of the caster on the area radius.
        bool enemyArea = source.Spell.HasEffect(SpellEffectName.ApplyAreaAuraEnemy);
        bool friendArea = !enemyArea && source.Spell.HasEffect(SpellEffectName.ApplyAreaAuraFriend);
        radius = radius > 0 ? ModFloat(state.Unit, source.Spell, SpellModOp.Radius, radius) : radius;
        bool inRangeOwned = !_areaInRangeScratchInUse;
        HashSet<ObjectGuid> inRange = inRangeOwned ? _areaInRangeScratch : [];
        _areaInRangeScratchInUse |= inRangeOwned;
        try
        {
            if (state.Unit.IsAlive)
            {
                bool membersOwned = !_areaMemberScratchInUse;
                List<ObjectGuid> members = membersOwned ? _areaMemberScratch : [];
                _areaMemberScratchInUse |= membersOwned;
                try
                {
                    if (enemyArea)
                    {
                        // vmangos AreaAura::Update, AREA_AURA_ENEMY (SpellAuras.cpp:689-695): every valid AoE target around the caster.
                        foreach (Unit unit in UnitsInRadius(map, state.Unit.X, state.Unit.Y, state.Unit.Z, radius))
                        {
                            if (!ReferenceEquals(unit, state.Unit) && IsEnemy(state.Unit, unit))
                            {
                                members.Add(unit.Guid);
                            }
                        }
                    }
                    else if (friendArea)
                    {
                        // vmangos AreaAura::Update, AREA_AURA_FRIEND (SpellAuras.cpp:682-687): every living unit around the caster that is friendly to it.
                        foreach (Unit unit in UnitsInRadius(map, state.Unit.X, state.Unit.Y, state.Unit.Z, radius))
                        {
                            if (!ReferenceEquals(unit, state.Unit) && unit.IsAlive && Relations.IsFriendly(state.Unit, unit))
                            {
                                members.Add(unit.Guid);
                            }
                        }
                    }
                    else
                    {
                        Groups.AppendGroupMembers(state.Unit, raid: false, members);
                    }

                    for (int m = 0; m < members.Count; m++)
                    {
                        ObjectGuid guid = members[m];
                        if (guid == state.Unit.Guid || Units.Find(state.Unit, guid) is not { IsAlive: true } member
                            || !ReferenceEquals(member.Map, map) || IsQuestSettlementPending(member))
                        {
                            continue;
                        }

                        float dx = member.X - state.Unit.X;
                        float dy = member.Y - state.Unit.Y;
                        float dz = member.Z - state.Unit.Z;
                        if ((dx * dx) + (dy * dy) + (dz * dz) > radius * radius)
                        {
                            continue;
                        }

                        inRange.Add(guid);
                        if (source.AreaChildren.TryGetValue(guid, out SpellAuraHolder? existing) && !existing.IsRemoved && ReferenceEquals(existing.Target, member))
                        {
                            continue;
                        }

                        // vmangos AreaAura::Update (SpellAuras.cpp:750-790): a member gets the rank its level allows (SelectAuraRankForLevel), with
                        // that rank's simple effect value, and nothing when even the first rank is too high or the aura is for players only.
                        if ((enemyArea ? source.Spell : SelectAuraRankForLevel(source.Spell, member.Level)) is not { } actual
                            || ((actual.AttributesEx3 & AreaAuraOnlyOnPlayer) != 0 && member is not Player))
                        {
                            continue;
                        }

                        bool holdsSpell = false;
                        List<SpellAuraHolder> held = GetAuras(member);
                        for (int h = 0; h < held.Count; h++)
                        {
                            if (!held[h].IsRemoved && (held[h].Spell.Id == source.Spell.Id || held[h].Spell.Id == actual.Id))
                            {
                                holdsSpell = true;
                                break;
                            }
                        }

                        if (holdsSpell)
                        {
                            continue;
                        }

                        bool lowerRank = !ReferenceEquals(actual, source.Spell);
                        var child = new SpellAuraHolder(actual, member, source.CasterGuid, source.CasterLevel, source.CasterOwner,
                            source.IsPermanent ? -1 : Math.Max(source.Duration, 1))
                        {
                            AreaParent = source,
                        };
                        foreach (SpellAura? aura in source.AuraSpan)
                        {
                            if (aura is not null && source.Spell.Effects[aura.EffectIndex].Effect is SpellEffectName.ApplyAreaAuraParty or SpellEffectName.ApplyAreaAuraFriend or SpellEffectName.ApplyAreaAuraEnemy)
                            {
                                if (enemyArea)
                                {
                                    // The source on the caster carries no aura type (vmangos SpellAuras.cpp:443-444); the child takes the effect's own.
                                    SpellEffectInfo effectInfo = source.Spell.Effects[aura.EffectIndex];
                                    child.SetAura(new SpellAura(aura.EffectIndex, effectInfo.AuraType, aura.Amount,
                                        ModifiedAmplitude(state.Unit, source.Spell, effectInfo), effectInfo.MiscValue, member.PowerType));
                                    continue;
                                }

                                uint amplitude = aura.Type == AuraType.ModPowerRegen
                                    ? source.Spell.Effects[aura.EffectIndex].Amplitude : aura.Amplitude;
                                int amount = lowerRank ? actual.Effects[aura.EffectIndex].BasePoints + actual.Effects[aura.EffectIndex].BaseDice : aura.Amount;
                                child.SetAura(new SpellAura(aura.EffectIndex, aura.Type, amount, amplitude, aura.MiscValue, member.PowerType));
                            }
                        }

                        if (child.IsEmpty)
                        {
                            continue;
                        }

                        source.AreaChildren[guid] = child;
                        AddAuraHolder(child);
                    }
                }
                finally
                {
                    if (membersOwned)
                    {
                        members.Clear();
                        _areaMemberScratchInUse = false;
                    }
                }
            }

            bool childrenOwned = !_areaChildScratchInUse;
            List<KeyValuePair<ObjectGuid, SpellAuraHolder>> children = childrenOwned ? _areaChildScratch : [];
            _areaChildScratchInUse |= childrenOwned;
            try
            {
                foreach (KeyValuePair<ObjectGuid, SpellAuraHolder> pair in source.AreaChildren)
                {
                    children.Add(pair);
                }

                for (int c = 0; c < children.Count; c++)
                {
                    (ObjectGuid guid, SpellAuraHolder child) = children[c];
                    if (child.IsRemoved)
                    {
                        source.AreaChildren.Remove(guid);
                    }
                    else if (!inRange.Contains(guid) && !IsQuestSettlementPending(child.Target)
                        && GetState(child.Target.Guid) is { } childState && ReferenceEquals(childState.Unit, child.Target))
                    {
                        // A held member retains its exact child until a later unheld update can
                        // evaluate its current party, range and source without changing staged state.
                        RemoveHolder(childState, child);
                    }
                }
            }
            finally
            {
                if (childrenOwned)
                {
                    children.Clear();
                    _areaChildScratchInUse = false;
                }
            }
        }
        finally
        {
            if (inRangeOwned)
            {
                inRange.Clear();
                _areaInRangeScratchInUse = false;
            }
        }
    }
}

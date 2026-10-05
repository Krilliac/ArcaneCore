using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
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

        foreach (SpellAuraHolder source in state.Auras.ToArray())
        {
            if (source.IsRemoved || !source.IsAreaSource)
            {
                continue;
            }

            float radius = source.Spell.Effects.Where(e => e.Effect == SpellEffectName.ApplyAreaAuraParty).Select(e => e.Radius).DefaultIfEmpty(0).Max();
            var inRange = new HashSet<ObjectGuid>();
            if (state.Unit.IsAlive)
            {
                foreach (ObjectGuid guid in Groups.GetGroupMembers(state.Unit, raid: false))
                {
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

                    if (GetAuras(member).Any(h => !h.IsRemoved && h.Spell.Id == source.Spell.Id))
                    {
                        continue;
                    }

                    var child = new SpellAuraHolder(source.Spell, member, source.CasterGuid, source.CasterLevel, source.CasterOwner,
                        source.IsPermanent ? -1 : Math.Max(source.Duration, 1))
                    {
                        AreaParent = source,
                    };
                    foreach (SpellAura? aura in source.Auras)
                    {
                        if (aura is not null && source.Spell.Effects[aura.EffectIndex].Effect == SpellEffectName.ApplyAreaAuraParty)
                        {
                            uint amplitude = aura.Type == AuraType.ModPowerRegen
                                ? source.Spell.Effects[aura.EffectIndex].Amplitude : aura.Amplitude;
                            child.SetAura(new SpellAura(aura.EffectIndex, aura.Type, aura.Amount, amplitude, aura.MiscValue, member.PowerType));
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

            foreach ((ObjectGuid guid, SpellAuraHolder child) in source.AreaChildren.ToArray())
            {
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
    }
}

using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// vmangos SpellMgr::SelectAuraRankForLevel (SpellMgr.cpp:1392-1436): the highest rank of a positive party or explicit-target buff that a
    /// unit of <paramref name="level"/> may hold, the first rank whose spell level is at most <c>level + 10</c>. Spells that need no selection
    /// (passive, unranked, no positive aura on a non-caster target, or already low enough) return themselves; null when even the first rank is
    /// too high.
    /// </summary>
    public SpellInfo? SelectAuraRankForLevel(SpellInfo spell, uint level)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (level + 10 >= spell.SpellLevel || spell.IsPassive)
        {
            return spell;
        }

        bool needRankSelection = false;
        for (int i = 0; i < spell.Effects.Count; i++)
        {
            SpellEffectInfo effect = spell.Effects[i];
            if (((effect.Effect == SpellEffectName.ApplyAura && (IsExplicitPositiveTarget(effect.TargetA) || IsAreaEffectPositiveTarget(effect.TargetA)))
                    || effect.Effect == SpellEffectName.ApplyAreaAuraParty)
                && spell.IsPositiveEffect(i, Store.Get))
            {
                needRankSelection = true;
                break;
            }
        }

        if (!needRankSelection || RankChains.Rank(spell.Id) == 0)
        {
            return spell;
        }

        for (uint id = spell.Id, depth = 0; id != 0 && depth < 30; id = RankChains.Previous(id), depth++)
        {
            if (Store.Get(id) is not { } rank)
            {
                break;
            }

            if (level + 10 >= rank.SpellLevel)
            {
                return rank;
            }
        }

        return null;
    }

    /// <summary>vmangos IsExplicitPositiveTarget (SpellEntry.h): targets the client names for a buff.</summary>
    internal static bool IsExplicitPositiveTarget(SpellImplicitTarget target)
        => target is SpellImplicitTarget.UnitFriend or SpellImplicitTarget.UnitParty or SpellImplicitTarget.UnitFriendChainHeal
            or SpellImplicitTarget.UnitRaid or (SpellImplicitTarget)61;

    /// <summary>vmangos IsAreaEffectPossitiveTarget (SpellEntry.h): party, raid and friendly area selectors.</summary>
    internal static bool IsAreaEffectPositiveTarget(SpellImplicitTarget target)
        => target is SpellImplicitTarget.EnumUnitsPartyWithinCasterRange or SpellImplicitTarget.EnumUnitsFriendAoeAtSrcLoc
            or SpellImplicitTarget.EnumUnitsFriendAoeAtDestLoc or SpellImplicitTarget.EnumUnitsPartyAoeAtSrcLoc
            or SpellImplicitTarget.EnumUnitsPartyAoeAtDestLoc or SpellImplicitTarget.UnitFriendAndParty
            or SpellImplicitTarget.EnumUnitsRaidWithinCasterRange or (SpellImplicitTarget)61;

    /// <summary>
    /// vmangos WorldSession::HandleCastSpellOpcode (SpellHandler.cpp): a buff the client aims at a unit is cast at the rank that unit's level
    /// allows; when no rank fits the original is kept and fails later with SPELL_FAILED_LOWLEVEL.
    /// </summary>
    private SpellInfo AutoSelectRankForTarget(Player player, SpellInfo spell, SpellCastTargets targets)
    {
        if (targets.Unit.IsEmpty || Units.Find(player, targets.Unit) is not { } target)
        {
            return spell;
        }

        return SelectAuraRankForLevel(spell, target.Level) ?? spell;
    }

    /// <summary>
    /// vmangos Spell::CheckCast (Spell.cpp:5487-5497): a player's non-item buff on another unit must already be the rank that unit may hold
    /// (the cast handler downranks it), else SPELL_FAILED_LOWLEVEL, unless SPELL_ATTR_EX2_ALLOW_LOW_LEVEL_BUFF.
    /// </summary>
    private SpellCastResult CheckLowLevelBuff(Unit caster, SpellInfo spell, Unit target, Items.Item? castItem)
    {
        if (caster is not Player || ReferenceEquals(caster, target) || castItem is not null || spell.HasAttribute(SpellAttributesEx2.AllowLowLevelBuff))
        {
            return SpellCastResult.CastOk;
        }

        return ReferenceEquals(SelectAuraRankForLevel(spell, target.Level), spell) ? SpellCastResult.CastOk : SpellCastResult.Lowlevel;
    }

    /// <summary>
    /// vmangos Spell::CheckTarget (Spell.cpp:7620-7628): a player's untriggered, non-item group-wide buff skips the units other than the
    /// explicit target whose level is too low for its rank.
    /// </summary>
    private bool SkipsLowLevelGroupBuffTarget(SpellCast cast, Unit unit, Unit? explicitTarget)
        => cast.Caster is Player && !ReferenceEquals(unit, cast.Caster) && !ReferenceEquals(unit, explicitTarget)
            && !cast.IsTriggered && cast.CastItem is null && cast.Spell.IsPositive
            && !ReferenceEquals(SelectAuraRankForLevel(cast.Spell, unit.Level), cast.Spell);
}

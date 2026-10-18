using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The hard-coded cases of vmangos <c>SpellMgr::GetSpellAllowedInLocationError</c> (SpellMgr.cpp:2650-2694, called by Spell::CheckCast at
/// Spell.cpp:5673-5676 for every caster, triggered or not). The player is the caster when it is a player:
/// <list type="bullet">
/// <item>Recall (22564, 22563) and Battle Standard (23538, 23539) need a player on Alterac Valley (map 30); otherwise REQUIRES_AREA.</item>
/// <item>The Warsong and Silverwing Flags (23333, 23335) need a player on Warsong Gulch (map 489); otherwise REQUIRES_AREA.</item>
/// <item>Waiting to Resurrect (2584) needs a player in a battleground; otherwise ONLY_BATTLEGROUNDS.</item>
/// <item>Spirit Heal Channel, Spirit Heal and the Resurrection Impact Visual (22011, 22012, 24171) need the caster on a battleground map; otherwise
/// ONLY_BATTLEGROUNDS.</item>
/// </list>
/// Deviations: "in a battleground" is read as being on a battleground map, and the Alterac Valley STATUS_WAIT_JOIN exclusion is not modelled.
/// A pet's owner is not looked up (<see cref="Rules.RuleUnitExtensions.IsLikePlayer"/> has no owner link yet). The spell_area table part
/// (world-database rows) is not covered.
/// </summary>
public sealed class LocationCastCheck : ISpellCastCheck
{
    public const uint AlteracValleyMap = 30;
    public const uint WarsongGulchMap = 489;

    /// <summary>The caster phase: the vmangos check needs no unit target (the target phase runs only when one exists).</summary>
    public SpellCheckPhase Phase => SpellCheckPhase.Caster;

    /// <summary>Spell.cpp:5673 comes after the shapeshift, stealth and caster aura state checks (:5349-5392).</summary>
    public int Order => SpellCastCheckOrder.CasterAuraState + 100;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        Unit caster = context.Caster;
        Maps.Map? map = caster.Map;
        return Evaluate(context.Spell.Id, map?.MapId ?? 0, map?.Template is { IsBattleground: true }, caster is Player);
    }

    /// <summary>The location result of <paramref name="spellId"/> for a caster on <paramref name="mapId"/>.</summary>
    public static SpellCastResult Evaluate(uint spellId, uint mapId, bool battlegroundMap, bool player) => spellId switch
    {
        22564 or 22563 or 23538 or 23539 => player && mapId == AlteracValleyMap && battlegroundMap ? SpellCastResult.CastOk : SpellCastResult.RequiresArea,
        23333 or 23335 => player && mapId == WarsongGulchMap && battlegroundMap ? SpellCastResult.CastOk : SpellCastResult.RequiresArea,
        2584 => player && battlegroundMap ? SpellCastResult.CastOk : SpellCastResult.OnlyBattlegrounds,
        22011 or 22012 or 24171 => battlegroundMap ? SpellCastResult.CastOk : SpellCastResult.OnlyBattlegrounds,
        _ => SpellCastResult.CastOk,
    };
}
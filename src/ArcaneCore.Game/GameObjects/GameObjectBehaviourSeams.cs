using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// The spells game objects cast (vmangos GameObject is a SpellCaster: goober and spell-caster spells, traps, linked traps, ritual spells).
/// The world wires it to its spell system; without it (tests, worlds without spells) every object spell is skipped. World thread only.
/// </summary>
public interface IGameObjectSpells
{
    /// <summary>
    /// Cast <paramref name="spellId"/> at <paramref name="target"/> for <paramref name="source"/>: as <paramref name="unitCaster"/> (a trap's
    /// owner, a ritual's caster) when given, otherwise as the object itself (vmangos <c>GameObject::CastSpell</c>). Triggered (no cost, no cast
    /// time, no range). False when the spell is unknown or the cast was refused.
    /// </summary>
    bool Cast(GameObject source, uint spellId, Unit target, Unit? unitCaster);

    /// <summary>The spell's maximum range (vmangos GetSpellMaxRange of its range index), or null for an unknown spell.</summary>
    float? MaxRange(uint spellId);

    /// <summary>Whether <paramref name="unit"/> is channelling a spell (vmangos <c>GetCurrentSpell(CURRENT_CHANNELED_SPELL)</c>).</summary>
    bool IsChanneling(Unit unit);

    /// <summary>
    /// A ritual helper's channelled animation (vmangos AddUniqueUse, GameObject.cpp:754-771: a triggered <c>Spell</c> of
    /// <c>summoningRitual.animSpell</c> by the helper with the ritual as its target and the channelling visual).
    /// </summary>
    void StartRitualAnimation(Player helper, uint animSpellId, GameObject ritual);

    /// <summary>
    /// The ritual spell (vmangos GameObject::Use, GameObject.cpp:1993-2027): cast by <paramref name="caster"/>, triggered, with the ritual as its
    /// object target and, when <paramref name="summonTarget"/> is set, that player (online anywhere: <c>sObjectMgr.GetPlayer</c>) as its unit
    /// target. True when the spell went off.
    /// </summary>
    bool CastRitualSpell(GameObject ritual, uint spellId, Unit caster, ObjectGuid summonTarget);

    /// <summary>vmangos FinishRitual (GameObject.cpp:806-809): the owner takes the cooldown of the spell that created the ritual.</summary>
    void StartCreatingSpellCooldown(Player owner, uint spellId);
}

/// <summary>
/// Gossip menus of game objects other than quest givers (vmangos GameObject::Use of a goober without page text, GameObject.cpp:1555-1562:
/// <c>PrepareGossipMenu(go, goober.gossipID)</c> and <c>SendPreparedGossip</c>). The world wires it to the quest and gossip feature.
/// </summary>
public interface IGameObjectGossip
{
    /// <summary>Show gossip menu <paramref name="menuId"/> of <paramref name="go"/> to <paramref name="player"/>; false when nothing was shown.</summary>
    bool OpenGossip(Player player, GameObject go, uint menuId);
}

/// <summary>
/// A flag stand clicked in a battleground (vmangos GameObject::Use, GAMEOBJECT_TYPE_FLAGSTAND, GameObject.cpp:1843-1870:
/// <c>bg->EventPlayerClickedOnFlag(player, go)</c>). The battleground area implements it.
/// </summary>
public interface IGameObjectFlagStands
{
    /// <summary>Whether <paramref name="player"/> may use battleground objects now (vmangos Player::CanUseBattleGroundObject).</summary>
    bool CanUseBattlegroundObject(Player player);

    /// <summary>Hand the click to the player's battleground; false when the player is in none.</summary>
    bool OnFlagClicked(Player player, GameObject flag);

    /// <summary>vmangos <c>RemoveSpellsCausingAura(SPELL_AURA_MOD_STEALTH / MOD_INVISIBILITY)</c> before the click is handed on.</summary>
    void BreakStealthAndInvisibility(Player player);
}

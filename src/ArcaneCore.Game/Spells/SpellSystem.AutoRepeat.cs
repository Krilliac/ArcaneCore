using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The auto-repeat spell slot (ranged (autorepeat lane)): Auto Shot (75) and wand Shoot (5019), the only two real auto-repeat
/// spells (Attributes ranged slot 0x2 plus AttributesEx2 0x20; classic-db spell_template rows 75 and 5019). Throw (2764) and
/// Shoot Bow/Gun/Crossbow (2480, 7918, 7919) are ordinary ranged abilities, not auto-repeat.
/// <para>
/// The model is vmangos' third spell slot: the toggled spell sits in <see cref="UnitSpellState.AutoRepeatCast"/> and never casts
/// itself (Spell::update, Spell.cpp:4097-4113 skips cast() for IsAutoRepeat). Each world update runs
/// <see cref="UpdateAutoRepeat"/> (vmangos Unit::_UpdateAutoRepeatSpell, Unit.cpp:2733-2778), which fires a triggered copy of the
/// spell whenever the ranged swing timer is ready, then restarts that timer from the (hasted) weapon speed.
/// </para>
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>vmangos spell Category of wand Shoot (Unit.cpp:2739, SpellCaster.cpp:1938 and 1952): the only auto-repeat that breaks other casts.</summary>
    internal const uint WandShootCategory = 351;

    /// <summary>The 0.5 s wind-up of a freshly started auto-repeat (Unit.cpp:2749-2750).</summary>
    internal const uint AutoRepeatWindUpMs = 500;

    /// <summary>
    /// A non-triggered auto-repeat spell was pressed (vmangos Spell::prepare, Spell.cpp:3379-3416, then SetCurrentCastedSpell, 3451,
    /// then SendSpellStart and AddGCD, 3466-3472). No cast bar and no cast: the toggle is acknowledged with SMSG_SPELL_START once.
    /// </summary>
    private SpellCastResult PrepareAutoRepeat(UnitSpellState state, Unit caster, SpellInfo spell, SpellCastTargets targets)
    {
        // "Prevent casting at cast another spell": a generic cast on the bar blocks, a channel and a running auto-repeat do not.
        if (state.CurrentCast is { State: SpellCastState.Preparing })
        {
            SendCastResult(caster, spell, SpellCastResult.SpellInProgress, triggered: false);
            return SpellCastResult.SpellInProgress;
        }

        Unit? unitTarget = ResolveUnitTarget(caster, targets);
        SpellCastResult result = CheckCast(state, spell, targets, unitTarget, triggered: false, strict: true, skipCooldown: true);
        // IsAcceptableAutorepeatError (Spell.cpp:3329-3332): toggling it on while moving is fine, and so is a self target that passed.
        bool acceptable = result is SpellCastResult.Moving or SpellCastResult.CastOk;
        if (!acceptable)
        {
            SendCastResult(caster, spell, result, triggered: false);
            return result;
        }

        // SetCurrentCastedSpell, case CURRENT_AUTOREPEAT_SPELL (SpellCaster.cpp:1950-1962).
        if (state.AutoRepeatCast is not null)
        {
            CancelAutoRepeat(caster); // InterruptSpell(CSpellType): the replaced spell is cancelled and the client told
        }

        if (spell.Category == WandShootCategory)
        {
            // generic autorepeats break generic non-delayed and channeled non-delayed spells
            if (state.CurrentCast is { } current)
            {
                Cancel(current);
            }
        }

        var cast = new SpellCast(spell, caster, targets, triggered: false, CastTimeFor(caster, spell), 0, DurationFor(caster, spell));
        state.AutoRepeatCast = cast;
        state.AutoRepeatFirstCast = true;

        InterruptAtCastStart(cast);
        NotifyPrepared(cast);
        SendToSet(caster, WorldOpcode.SmsgSpellStart, SpellPackets.BuildSpellStart(
            caster.Guid, caster.Guid, spell.Id, WithAmmoFlag(SpellCastFlags.Unknown2, spell), (uint)cast.CastTime, targets,
            RangedSpellFacts.IsRanged(spell) ? GetAmmoVisual(caster) : default), includeSelf: true);
        AddGlobalCooldown(state, spell);
        return SpellCastResult.CastOk;
    }

    /// <summary>
    /// A generic (non-triggered, non-auto-repeat) cast entered the cast slot (vmangos SetCurrentCastedSpell, case
    /// CURRENT_GENERIC_SPELL, SpellCaster.cpp:1936-1946): a wand stops, and any auto-repeat gets a new 0.5 s wind-up.
    /// </summary>
    private void OnGenericCastStarted(UnitSpellState state, SpellInfo spell)
    {
        _ = spell;
        if (state.AutoRepeatCast is not { } autoRepeat)
        {
            return;
        }

        if (autoRepeat.Spell.Category == WandShootCategory)
        {
            CancelAutoRepeat(state.Unit);
        }

        state.AutoRepeatFirstCast = true;
    }

    /// <summary>
    /// Stop the toggled auto-repeat spell (vmangos InterruptSpell(CURRENT_AUTOREPEAT_SPELL), SpellCaster.cpp:2083-2100): a player's client is
    /// told with SMSG_CANCEL_AUTO_REPEAT (empty) first, then the spell is cancelled like any cast on the bar (SMSG_SPELL_FAILED_OTHER and
    /// SMSG_CAST_RESULT interrupted). For CMSG_CANCEL_AUTO_REPEAT_SPELL, CMSG_CANCEL_CAST, death, logout and the other interrupt sources.
    /// </summary>
    /// <returns>True when a spell was cancelled.</returns>
    public bool CancelAutoRepeat(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid)?.AutoRepeatCast is not { } cast)
        {
            return false;
        }

        if (unit is Player player)
        {
            player.Session.Send(WorldOpcode.SmsgCancelAutoRepeat, []);
        }

        if (cast.State != SpellCastState.Finished)
        {
            Cancel(cast);
        }
        else
        {
            Finish(cast);
        }

        return true;
    }

    /// <summary>
    /// CMSG_SET_SELECTION while an auto-repeat spell runs (vmangos WorldSession::HandleSetSelectionOpcode, MiscHandler.cpp:416-428): a
    /// selection that is gone or is not a valid attack target clears the spell's target and cancels it; a valid one in the same map
    /// becomes the new target without a second SMSG_SPELL_START.
    /// </summary>
    public void RetargetAutoRepeat(Player player, ObjectGuid selection)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (GetState(player.Guid)?.AutoRepeatCast is not { } cast)
        {
            return;
        }

        Unit? unit = selection.IsEmpty ? null : Units.Find(player, selection);
        if (unit is null || !Relations.IsHostile(player, unit))
        {
            CancelAutoRepeat(player);
            return;
        }

        if (!unit.IsInWorld || unit.Map != player.Map)
        {
            return;
        }

        cast.Targets = SpellCastTargets.ForUnit(unit.Guid);
    }

    /// <summary>Whether <paramref name="unit"/> has an auto-repeat spell toggled on (vmangos GetCurrentSpell(CURRENT_AUTOREPEAT_SPELL) != null).</summary>
    public bool HasAutoRepeat(Unit unit) => GetState(unit.Guid)?.AutoRepeatCast is not null;

    /// <summary>
    /// vmangos Unit::_UpdateAutoRepeatSpell (Unit.cpp:2733-2778) plus the target check at the top of Spell::update (Spell.cpp:4005-4009).
    /// </summary>
    private void UpdateAutoRepeat(UnitSpellState state, SpellCast master)
    {
        Unit unit = state.Unit;
        if (master.Targets.Unit is { IsEmpty: false } targetGuid && Units.Find(unit, targetGuid) is null)
        {
            CancelAutoRepeat(unit); // the target left the map or the world
            return;
        }

        // "realtime" interrupts: moving (players) or any other cast or channel in progress
        if ((unit is Player player && IsMoving(player)) || state.CurrentCast is { State: not SpellCastState.Finished })
        {
            if (master.Spell.Category == WandShootCategory)
            {
                CancelAutoRepeat(unit); // cancel wand shoot
                return;
            }

            state.AutoRepeatFirstCast = true; // 0.5 s wind-up again
            return;
        }

        UnitCombat combat = unit.Combat;
        if (state.AutoRepeatFirstCast && combat.GetAttackTimer(WeaponAttackType.RangedAttack) < AutoRepeatWindUpMs)
        {
            combat.SetAttackTimer(WeaponAttackType.RangedAttack, AutoRepeatWindUpMs);
        }

        state.AutoRepeatFirstCast = false;
        if (!combat.IsAttackReady(WeaponAttackType.RangedAttack))
        {
            return;
        }

        Unit? target = ResolveUnitTarget(unit, master.Targets);
        SpellCastResult result = CheckCast(state, master.Spell, master.Targets, target, triggered: false, strict: true, skipCooldown: true);
        if (result is SpellCastResult.Moving or SpellCastResult.NotReady)
        {
            return; // just delay it
        }

        if (result != SpellCastResult.CastOk)
        {
            CancelAutoRepeat(unit); // InterruptSpell(CURRENT_AUTOREPEAT_SPELL): the client is told it was interrupted, not why
            return;
        }

        Prepare(unit, master.Spell, master.Targets, triggered: true, autoRepeatShot: true);
        combat.ResetAttackTimer(WeaponAttackType.RangedAttack);
        if (unit is Player standing)
        {
            standing.SetStandState(StandState.Stand);
        }
    }
}

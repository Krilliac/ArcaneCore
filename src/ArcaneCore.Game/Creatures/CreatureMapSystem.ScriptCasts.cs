using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The relay cast-spell script command (15) with a source that is no creature, which the spell scripts need (a player's own cast starts them, and
/// SCRIPT_FLAG_REVERSE_DIRECTION makes the player the source): cmangos ScriptAction::ExecuteDbscriptCommand, DBScripts/ScriptMgr.cpp:2155-2202.
/// <para>
/// Not ported (cmangos does them, ArcaneCore does not): the game object as the spell's original caster (:2180), TRIGGERED_DO_NOT_PROC on every script
/// cast (:2180, :2200), the full datalong2 cast flags (only bit 0x01 is read), a game object spell target (setGOTarget) and a destination target
/// (TARGET_FLAG_DEST_LOCATION / TARGET_FLAG_SOURCE_LOCATION).
/// </para>
/// </summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// The CAST_SPELL step of a source that is not a creature. A game object source makes the target cast the spell on itself, triggered
    /// (:2173-2181: GO casts are not implemented in cmangos); any other unit casts it like a creature does, at the target unless
    /// COMMAND_ADDITIONAL, triggered when datalong2 has TRIGGERED_OLD_TRIGGERED (LogIfNotUnit then Unit::CastSpell, :2183-2200). The spell is
    /// chosen as for a creature, so the random draw is the same.
    /// </summary>
    private bool CastSpellByNonCreature(RelayScriptStep step, WorldObject? source, WorldObject? target)
    {
        if (source is GameObjects.GameObject)
        {
            if (target is not Unit self)
            {
                ReportRelay(step, "CAST_SPELL by a game object without a unit target");
                return false;
            }

            CastSpellByUnit(self, ScriptCastSpellId(step), self, triggered: true);
            return false;
        }

        if (source is not Unit caster)
        {
            ReportRelay(step, "CAST_SPELL by a non-unit");
            return false;
        }

        uint spell = ScriptCastSpellId(step);
        CastSpellByUnit(caster, spell, ScriptCastTarget(step, caster, target, spell), triggered: (step.DataLong2 & 0x01) != 0);
        return false;
    }

    /// <summary>
    /// The unit a script cast aims at: the target, except that a spell with SPELL_ATTR_EX_EXCLUDE_CASTER cast on its own source has no target,
    /// and COMMAND_ADDITIONAL clears it (cmangos ScriptMgr.cpp:2167-2170, in that order).
    /// </summary>
    private Unit? ScriptCastTarget(RelayScriptStep step, WorldObject source, WorldObject? target, uint spell)
    {
        if (ReferenceEquals(target, source) && _ai.UnitSpells?.ExcludesCaster(spell) == true)
        {
            target = null;
        }

        return (step.DataFlags & FlagCommandAdditional) != 0 ? null : target as Unit;
    }

    /// <summary>datalong, or one of the filled dataint..dataint4 at random (cmangos urand(0, filledCount), :2158-2164).</summary>
    private uint ScriptCastSpellId(RelayScriptStep step)
    {
        int filled = step.DataInts.TakeWhile(i => i != 0).Count();
        uint spell = step.DataLong;
        if (filled > 0 && _random.Next(0, filled + 1) is int pick and > 0)
        {
            spell = (uint)step.DataInts[pick - 1];
        }

        return spell;
    }
}

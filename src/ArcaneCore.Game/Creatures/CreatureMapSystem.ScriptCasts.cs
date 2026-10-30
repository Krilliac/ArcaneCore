using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// SCRIPT_COMMAND_CAST_SPELL (15) with a source that is no creature, which the spell scripts need (a player's own cast starts them, and
/// SCRIPT_FLAG_REVERSE_DIRECTION makes the player the source): cmangos ScriptAction::ExecuteDbscriptCommand, DBScripts/ScriptMgr.cpp:2155-2202.
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
        Unit? at = (step.DataFlags & FlagCommandAdditional) != 0 ? null : target as Unit;
        CastSpellByUnit(caster, spell, at, triggered: (step.DataLong2 & 0x01) != 0);
        return false;
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

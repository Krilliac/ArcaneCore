using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The spell DB scripts (<c>dbscripts_on_spell</c>, <see cref="DbScriptKind.Spell"/>): the database fallback at the end of vmangos
/// Spell::EffectScriptEffect (SpellEffects.cpp:4565-4571), run through the map's DB script executor
/// (<see cref="CreatureMapSystem.StartDbScript"/>). SCRIPT_EFFECT starts it (retail); a DUMMY-only spell no code claims does too (fallback, see <see cref="CanEffectStartDbScript"/>).
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// vmangos starts the <c>spell_scripts</c> table from SPELL_EFFECT_SCRIPT_EFFECT: Spell::EffectScriptEffect's trailing "normal DB scripted
    /// effect" (SpellEffects.cpp:4565-4571), once per SCRIPT_EFFECT effect that hits a target. vmangos EffectDummy never does, because its
    /// DUMMY spells are hard-coded in C++. ArcaneCore has not ported every one of those cases, so a DUMMY effect is the documented fallback
    /// (cmangos starts the table from DUMMY as well, ScriptMgr.cpp:67-106): it starts the row from the FIRST DUMMY effect of a spell that has no
    /// SCRIPT_EFFECT effect, and only when no code claims the spell (<see cref="IsClaimedByCode"/>). Of the DUMMY-only ids with imported rows,
    /// 11886-11889 (CaptureDespawnScript) and 25716 (ForceBowScript) are claimed by an <see cref="ISpellScript"/>, so their rows never run;
    /// 9096, 11365, 11885, 16378, 19250, 21052, 21914, 21917 and 23359 have no code claim, so their rows are the only implementation. 29182 and
    /// 29188 (AhnQirajTeleportScript) are claimed by code too. Every SCRIPT_EFFECT effect starts the row (vmangos runs the trailing block once
    /// per effect); the cmangos priority rule let only the first of several SCRIPT_EFFECT effects start it.
    /// A spell with both a DUMMY and a SCRIPT_EFFECT effect starts its row from the SCRIPT_EFFECT only.
    /// </summary>
    internal static bool CanEffectStartDbScript(SpellInfo spell, int effectIndex)
    {
        SpellEffectName effect = spell.Effects[effectIndex].Effect;
        if (effect == SpellEffectName.ScriptEffect)
        {
            return true;
        }

        if (effect != SpellEffectName.Dummy)
        {
            return false;
        }

        for (int i = 0; i < spell.Effects.Count; i++)
        {
            SpellEffectName other = spell.Effects[i].Effect;
            if (other == SpellEffectName.ScriptEffect || (other == SpellEffectName.Dummy && i < effectIndex))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The DUMMY effect: no generic behaviour (spell scripts hook it); the spell's DB script is the fallback, see <see cref="CanEffectStartDbScript"/>.</summary>
    private void EffectDummy(SpellEffectContext context) => StartDbSpellScript(context);

    /// <summary>
    /// Start the spell's DB script for this effect and target when the spell has one and no spell script in code claims the spell (an
    /// <see cref="ISpellScript"/> or a <see cref="ScriptEffectRegistry"/> entry): ArcaneCore's code scripts stand where cmangos' C++ cases
    /// <c>return</c> before the fallback, so a ported script and an imported row never both act. The source is the true caster (a game
    /// object casting by itself, vmangos <c>m_casterGo</c>, cmangos <c>m_trueCaster</c>), the target the effect's unit target, or its game
    /// object for an effect aimed at one (cmangos TARGET_TYPE_GAMEOBJECT / TARGET_TYPE_LOCK). A script starts on every hit
    /// (SCRIPT_EXEC_PARAM_NONE). Nothing happens on a map without a creature system.
    /// </summary>
    internal void StartDbSpellScript(SpellEffectContext context)
    {
        if (!CanEffectStartDbScript(context.Spell, context.EffectIndex)
            || context.Caster.Map is not { } map
            || map.FindUpdater<CreatureMapSystem>() is not { } scripts
            || !scripts.HasDbScript(DbScriptKind.Spell, context.Spell.Id)
            || IsClaimedByCode(context.Spell.Id))
        {
            return;
        }

        WorldObject source = (WorldObject?)context.CasterGameObject ?? context.Caster;
        WorldObject target = context.Effect.TargetA is SpellImplicitTarget.GameObject or SpellImplicitTarget.GameObjectItem
            && map.FindObject(context.Cast.Targets.GameObject) is { } gameObject
            ? gameObject
            : context.Target;
        scripts.StartDbScript(DbScriptKind.Spell, context.Spell.Id, source, target);
    }

    private bool IsClaimedByCode(uint spellId)
    {
        if (ScriptEffectRegistry.For(this).SpellIds.Contains(spellId))
        {
            return true;
        }

        foreach (ISpellCastObserver observer in Observers)
        {
            if (observer is SpellScriptDispatcher dispatcher && dispatcher.Registry.Find(spellId) is not null)
            {
                return true;
            }
        }

        return false;
    }
}

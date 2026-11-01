using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The spell DB scripts (<c>dbscripts_on_spell</c>, <see cref="DbScriptKind.Spell"/>): the database fallback at the end of vmangos
/// Spell::EffectScriptEffect (SpellEffects.cpp:4565-4571), run through the map's DB script executor
/// (<see cref="CreatureMapSystem.StartDbScript"/>). Only SCRIPT_EFFECT starts it (retail; cmangos also starts it from DUMMY).
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// vmangos starts the <c>spell_scripts</c> table from SPELL_EFFECT_SCRIPT_EFFECT only: Spell::EffectScriptEffect's trailing "normal DB scripted
    /// effect" (SpellEffects.cpp:4565-4571). EffectDummy never does (it has no ScriptsStart of sSpellScripts, only the event start at :1775), so a DUMMY effect
    /// runs no row. The call is made once per SCRIPT_EFFECT effect that hits a target, as vmangos does; a spell with a ported handler never reaches
    /// it (<see cref="IsClaimedByCode"/>). cmangos' DUMMY and trigger-effect priority scheme (ScriptMgr.cpp:67-106) is not reproduced.
    /// </summary>
    internal static bool CanEffectStartDbScript(SpellInfo spell, int effectIndex)
        => spell.Effects[effectIndex].Effect == SpellEffectName.ScriptEffect;

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

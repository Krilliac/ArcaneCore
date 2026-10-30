using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The spell DB scripts (<c>dbscripts_on_spell</c>, <see cref="DbScriptKind.Spell"/>): the database fallback at the end of cmangos
/// Spell::EffectDummy and Spell::EffectScriptEffect (Spells/SpellEffects.cpp:1517-1532 and :4176-4191) and of vmangos
/// Spell::EffectScriptEffect (SpellEffects.cpp:4565-4571), run through the map's DB script executor
/// (<see cref="CreatureMapSystem.StartDbScript"/>).
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// cmangos GetSpellStartDBScriptPriority (DBScripts/ScriptMgr.cpp:67-85): SCRIPT_EFFECT 10, DUMMY 9. The trigger effects that name a spell
    /// missing from the table (5 and 4 there) are not hooked: no imported row needs them, and vmangos starts the table from SCRIPT_EFFECT only.
    /// </summary>
    private static int DbScriptPriority(SpellEffectInfo effect) => effect.Effect switch
    {
        SpellEffectName.ScriptEffect => 10,
        SpellEffectName.Dummy => 9,
        _ => 0,
    };

    /// <summary>
    /// cmangos ScriptMgr::CanSpellEffectStartDBScript (ScriptMgr.cpp:88-106): only the effect of the highest priority starts the script, and of
    /// two with the same priority the lower index, so a spell with two DUMMY effects runs its script once per target, not twice.
    /// </summary>
    internal static bool CanEffectStartDbScript(SpellInfo spell, int effectIndex)
    {
        int priority = DbScriptPriority(spell.Effects[effectIndex]);
        if (priority == 0)
        {
            return false;
        }

        for (int i = 0; i < spell.Effects.Count; i++)
        {
            int other = DbScriptPriority(spell.Effects[i]);
            if (other > priority || (other == priority && i < effectIndex))
            {
                return false;
            }
        }

        return true;
    }

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

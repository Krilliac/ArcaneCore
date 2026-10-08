namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The base of a ported ScriptDev2 boss (mangos-classic AI/ScriptDevAI/base: <c>ScriptedAI</c> and <c>CombatAI</c>):
/// <list type="bullet">
/// <item>it attacks a hostile unit that comes into its aggro range (<c>ScriptedAI::MoveInLineOfSight</c> calls <c>UnitAI::MoveInLineOfSight</c>,
/// which aggroes when the creature can initiate an attack), the same rule <see cref="AggressorAI"/> and EventAI follow here; a passive
/// react state or an unselectable creature still does not (<see cref="CreatureMapSystem.CanInitiateAttack"/>);</item>
/// <item><see cref="Reset"/> runs on spawn and respawn (<c>JustRespawned</c>) and again at every evade (<c>ScriptedAI::EnterEvadeMode</c> ends
/// with <c>Reset()</c>), so a wipe leaves no phase, enrage or shield flag behind for the next pull.</item>
/// </list>
/// A script that needs a different evade (Mograine and Whitemane fail their event instead) overrides <see cref="OnEvade"/>.
/// </summary>
public abstract class ScriptedAI(Creature creature) : CreatureAI(creature)
{
    /// <inheritdoc />
    public override bool AggroesOnSight => true;

    /// <summary>ScriptDev2 <c>Reset()</c>: the script's timers and flags back to their start values.</summary>
    protected abstract void Reset();

    /// <summary>ScriptDev2 <c>JustRespawned</c> → <c>Reset()</c> (also the first spawn: the constructor calls it there).</summary>
    public override void OnRespawn() => Reset();

    /// <summary>ScriptDev2 <c>EnterEvadeMode</c> → <c>Reset()</c>.</summary>
    public override void OnEvade() => Reset();
}

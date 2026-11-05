using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Instances.Scripts.BlackrockDepths;

/// <summary>
/// ScriptName <c>boss_doomrel</c> (entry 9039), clean-room spec 230-boss_doomrel.md: Doom'rel's combat. It hosts the entry's EventAI rows
/// (<see cref="CreatureAiFactory.RegisterEventAiScript"/>), which carry every timer and spell of spec section 3: on aggro the immune-to-player
/// flag (0x100) is removed and 15092 (three voidwalkers) and 13787 (Demon Armor) are cast on self; Shadow Bolt Volley 15245 every 8-12 s,
/// Immolate 12742 from 18-20 s then every 25 s and Curse of Weakness 12493 from 5-8 s then every 40-45 s, the last two on a random hostile target;
/// reaching home sets the Tomb of the Seven to Fail and despawns his guardians (section 5). Every hook calls <c>base</c> so the rows fire as under
/// the plain EventAI (spec section 7).
/// <para>
/// This AI does NOT orchestrate the dwarf rounds (spec section 2): the instance (<c>BlackrockDepthsInstance.CallNextDwarf</c>) owns them, it
/// gives Doom'rel his hostile faction when his round comes, and his gossip stays <see cref="DoomrelGossip"/> (registered by entry, not here).
/// </para>
/// <para>
/// Spec section 8, ambiguity 3 (voidwalker target): the recommended behaviour is taken, a summoned Voidwalker Minion (8996) is sent at a random
/// player of Doom'rel's threat list at once. Ambiguities 1 and 2 (trigger and timers) follow the fixture rows, as recommended; they are data.
/// State: the EventAI engine's only. World thread.
/// </para>
/// </summary>
public sealed class DoomrelAI(Creature creature, CreatureAiContent content) : CreatureEventAI(creature, content)
{
    public const string ScriptName = "boss_doomrel";

    public const uint Entry = 9039;

    /// <summary>"Voidwalker Minion", the guardian of spell 15092.</summary>
    public const uint VoidwalkerMinion = 8996;

    public override void OnJustSummoned(Creature summoned)
    {
        base.OnJustSummoned(summoned);
        if (summoned.Template.Entry == VoidwalkerMinion && SelectRandomAttackingTarget(playerOnly: true) is { } target)
        {
            summoned.AI?.AttackStart(target);
        }
    }
}

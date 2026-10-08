namespace ArcaneCore.Game.Creatures;

/// <summary>How proximity aggro is driven.</summary>
public enum AggroScanMode
{
    /// <summary>
    /// vmangos behaviour: a unit that moves (or joins the map) schedules an AI notify after
    /// <see cref="CreatureOptions.AiRelocationNotifyDelayMs"/>; the notify visits every creature (for a player) or every
    /// player (for a creature) within <see cref="CreatureOptions.MaxCreatureAttackRadius"/> times the aggro rate and calls
    /// <c>MoveInLineOfSight</c> (Objects/Unit.cpp:10082-10113, Maps/GridNotifiersImpl.h:57-120). Standing still
    /// triggers nothing.
    /// </summary>
    Relocation,

    /// <summary>Development: every creature checks every player in the map each tick (the original implementation).</summary>
    Poll,
}

public sealed partial class CreatureOptions
{
    /// <summary>How proximity aggro is driven (<c>Creatures:AggroScanMode</c>). <see cref="AggroScanMode.Relocation"/> is retail behaviour.</summary>
    public AggroScanMode AggroScanMode { get; set; } = AggroScanMode.Relocation;

    /// <summary>Visibility.AIRelocationNotifyDelay (ms): vmangos 1000 (World.cpp:829, mangosd.conf.dist.in:2605).</summary>
    public uint AiRelocationNotifyDelayMs { get; set; } = 1000;

    /// <summary>MaxCreaturesAttackRadius (yd): the relocation notify visits objects this far around the mover times the aggro rate; vmangos 40 (World.cpp:565, mangosd.conf.dist.in:1528).</summary>
    public float MaxCreatureAttackRadius { get; set; } = 40.0f;

    /// <summary>
    /// Milliseconds a creature cannot initiate attacks after it respawns (vmangos Creature::SetTempPacified(5000) on respawn,
    /// Objects/Creature.cpp:877-878). 0 disables.
    /// </summary>
    public uint RespawnPacifyMs { get; set; } = 5000;

    /// <summary>
    /// How often a creature in combat runs its periodic leash checks, in milliseconds of world time (vmangos
    /// <c>tickTime() % 3000 &lt;= diff</c>, Objects/Creature.cpp:976). 0 turns the template hard leash off.
    /// </summary>
    public uint LeashCheckIntervalMs { get; set; } = 3000;

    /// <summary>
    /// Whole seconds after the leash extension clock was last set before a victim outside the threat area leashes the creature
    /// (vmangos hard-coded 12, Objects/Creature.cpp:2813).
    /// </summary>
    public uint LeashExtensionSeconds { get; set; } = 12;

    /// <summary>Send SMSG_AI_REACTION(hostile) when a creature starts attacking (vmangos Creature::SendAIReaction from Unit::Attack); the client plays the aggro sound from it.</summary>
    public bool SendAiReaction { get; set; } = true;

    /// <summary>
    /// Development: add both bounding radii to the aggro range and subtract them from the vertical test. Retail (false)
    /// measures the plain distance (<c>IsWithinDistInMap(..., SizeFactor::None)</c>, AI/BasicAI.cpp:61-67).
    /// </summary>
    public bool AggroUsesBoundingRadius { get; set; }

    /// <summary>
    /// Creatures aggro on other creatures in sight (<c>Creatures:CreatureAggroOnCreatures</c>): a moving creature notifies the creatures
    /// around it as well as the players (mangos CreatureCreatureRelocationWorker, WorldHandlers/GridNotifiersImpl.h:67-84), so a
    /// hostile mob and a guard, or a mob and an aggressive pet, acquire each other. Retail is true; false keeps the player-only
    /// notifies of the earlier build (cheaper on maps full of wanderers).
    /// </summary>
    public bool CreatureAggroOnCreatures { get; set; } = true;

    /// <summary>
    /// A guard also attacks a <em>creature</em> that is fighting a creature the guard is friendly to (<c>Creatures:GuardsDefendFriendlies</c>).
    /// A player who attacks a unit the guard is friendly to is always attacked, from up to 30 yd (vmangos GuardAI::IsAttackingPlayerOrFriendly
    /// and MoveInLineOfSight, AI/GuardAI.cpp:35-77, which look at players only). mangos keeps the general clause commented out
    /// (Object/GuardAI.cpp:74), so for creature attackers the retail behaviour is UNVERIFIED; on by default because a guard that watches a
    /// civilian being killed is the worse mistake.
    /// </summary>
    public bool GuardsDefendFriendlies { get; set; } = true;
}

namespace ArcaneCore.Game.Instances.Scripts.BlackrockDepths;

/// <summary>
/// The world-wide ScriptName registrations of the Blackrock Depths NPC scripts that need no instance reference (Theldren Trigger, Doom'rel),
/// called once by the <see cref="Creatures.CreatureAiFactory"/> constructor. Mistress Nagmara and Private Rocknot are registered by entry by the
/// instance itself (they find each other through it). Doom'rel's gossip stays <see cref="DoomrelGossip"/>, registered by entry with the gossip services.
/// </summary>
public static class DepthsNpcScripts
{
    public static void Register(Creatures.CreatureAiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        factory.Register(TheldrenTriggerAI.ScriptName, c => new TheldrenTriggerAI(c));
        factory.RegisterEventAiScript(DoomrelAI.ScriptName, (c, content) => new DoomrelAI(c, content));
    }
}

namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureOptions
{
    /// <summary>
    /// <c>Creatures:StealthAlertEnabled</c>: a hostile creature that notices a stealthed player just outside its detection range reacts
    /// (SMSG_AI_REACTION alert, it stops and turns to the player); vmangos CreatureAI::OnMoveInStealth, AI/CreatureAI.cpp:349-385. Retail is true.
    /// </summary>
    public bool StealthAlertEnabled { get; set; } = true;

    /// <summary>Milliseconds between two alerts of one creature (<c>Creatures:StealthAlertCooldownMs</c>): vmangos 10000 (AI/CreatureAI.cpp:366-367).</summary>
    public uint StealthAlertCooldownMs { get; set; } = 10000;
}

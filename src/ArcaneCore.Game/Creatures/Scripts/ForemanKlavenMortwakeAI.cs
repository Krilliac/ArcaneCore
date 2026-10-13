using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Foreman Klaven Mortwake (entry 7053, Westfall): mangos-classic ScriptDev2 <c>npc_foreman_klaven_mortwake</c>
/// (eastern_kingdoms/westfall.cpp). When his stealth alert fires on a stealthed player just outside his detection range, he calls it out
/// to that player (broadcast text 3092, OnStealthAlert).
/// </summary>
public sealed class ForemanKlavenMortwakeAI(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = 7053;
    public const int SayStealthAlert = 3092;

    protected override void Reset()
    {
    }

    public override void OnMoveInStealth(Unit who)
    {
        ArgumentNullException.ThrowIfNull(who);
        if (System is { } system && system.CanTriggerAlert(Me, who))
        {
            system.TriggerAlert(Me, who);
            system.SayText(Me, SayStealthAlert, who);
        }
    }
}

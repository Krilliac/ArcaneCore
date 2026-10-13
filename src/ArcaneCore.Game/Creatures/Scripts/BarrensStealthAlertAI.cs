using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// The Barrens' Venture Co. stealth-alert speakers: mangos-classic ScriptDev2 <c>npc_gallywix</c>, <c>npc_foreman_silixiz</c> and
/// <c>npc_venture_co_lookout</c> (kalimdor/the_barrens.cpp). When the creature's stealth alert fires on a stealthed player just outside its
/// detection range, it calls out to that player (OnStealthAlert).
/// </summary>
public abstract class StealthAlertSpeakerAI(Creature creature) : ScriptedAI(creature)
{
    /// <summary>The line said to the player the alert fired on.</summary>
    protected abstract int AlertText();

    protected override void Reset()
    {
    }

    public override void OnMoveInStealth(Unit who)
    {
        ArgumentNullException.ThrowIfNull(who);
        if (System is { } system && system.CanTriggerAlert(Me, who))
        {
            system.TriggerAlert(Me, who);
            system.SayText(Me, AlertText(), who);
        }
    }
}

/// <summary>Gallywix (entry 7288): "Who would be foolish enough to enter the tower of the Grand Foreman?" (-1010005).</summary>
public sealed class GallywixAI(Creature creature) : StealthAlertSpeakerAI(creature)
{
    public const uint Entry = 7288;
    public const int SayStealthAlert = -1010005;

    protected override int AlertText() => SayStealthAlert;
}

/// <summary>Foreman Silixiz (entry 7287): "*cough* who goes there? *cough*" (-1010006).</summary>
public sealed class ForemanSilixizAI(Creature creature) : StealthAlertSpeakerAI(creature)
{
    public const uint Entry = 7287;
    public const int SayStealthAlert = -1010006;

    protected override int AlertText() => SayStealthAlert;
}

/// <summary>Venture Co. Lookout (entry 7307): one of "Did you hear that?" (-1010007) or "I smell rogue." (-1010008).</summary>
public sealed class VentureCoLookoutAI(Creature creature) : StealthAlertSpeakerAI(creature)
{
    public const uint Entry = 7307;
    public const int SayStealthAlert1 = -1010007, SayStealthAlert2 = -1010008;

    protected override int AlertText() => System?.RandomInt(0, 1) == 0 ? SayStealthAlert2 : SayStealthAlert1;
}

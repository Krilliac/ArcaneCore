using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>Scaffold: inert until the fight no-progress watch is written.</summary>
internal sealed class PlayerbotFightWatch
{
    internal const uint StallMs = 20_000;

    internal bool Observe(ObjectGuid target, uint health, float distance, uint nowMs) => false;

    internal void Reset()
    {
    }
}

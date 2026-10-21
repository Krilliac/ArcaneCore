using System.Numerics;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// A scenario stay: the autonomous bot waits at <see cref="Centre"/> on <see cref="MapId"/> (within <see cref="Radius"/> yards) instead of
/// choosing a destination, until the lease <see cref="UntilMs"/> (a world-clock time) runs out. Unlike a <see cref="PlayerbotDefendPost"/> it
/// is not a hold: the group coordinator still counts the bot free (a siege can enlist it), the bot raises no PvP flag and scans no post,
/// and its own defence and retreat run as usual. <see cref="StayId"/> names the scenario group that set it
/// (<c>EventScenarioPostIds.For</c>). Memory only: a restart ends it, and the lease ends it if its owner stops renewing it.
/// </summary>
internal sealed record PlayerbotStay(uint MapId, Vector3 Centre, float Radius, int StayId, uint UntilMs);

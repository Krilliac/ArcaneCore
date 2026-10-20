using System.Numerics;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Playerbots.EventScenarios;

/// <summary>What the engine needs to know about a running bot (world thread).</summary>
/// <param name="Alive">Alive and not a ghost.</param>
/// <param name="InCombat">In combat.</param>
/// <param name="Level">Its level.</param>
/// <param name="Team">0 Alliance, 1 Horde.</param>
/// <param name="MapId">The map it stands on.</param>
/// <param name="Position">Where it stands.</param>
/// <param name="HeldBy">Why another system owns it (scripted, in a battleground, in a group or party), or null when it is free to claim.</param>
internal readonly record struct ScenarioBotProbe(bool Alive, bool InCombat, int Level, byte Team, uint MapId, Vector3 Position, string? HeldBy);

/// <summary>What starting a siege came to.</summary>
/// <param name="Started">The siege runs.</param>
/// <param name="SiegeId">Its id (the coordinator's), 0 when it did not start.</param>
/// <param name="Refusal">Why it did not start.</param>
/// <param name="Enlisted">Bots in the raids.</param>
/// <param name="Skipped">Bots the coordinator turned away.</param>
internal readonly record struct ScenarioSiegeResult(bool Started, int SiegeId, string? Refusal, int Enlisted, int Skipped);

/// <summary>
/// The server the scenario engine acts on: the bot registry, the bots' state, and the commands it would otherwise get from a GM. The
/// engine owns no clock or thread of its own; every member is called on the world thread, and every <c>done</c> callback is called on it.
/// The real implementation is <see cref="ManagedEventScenarioHost"/>; tests use a fake.
/// </summary>
internal interface IEventScenarioHost
{
    /// <summary>The world clock in milliseconds (wraps; the engine differences it).</summary>
    uint NowMs { get; }

    /// <summary>The registered bots (a snapshot). Called at most once per tick, and only while some group still needs a bot.</summary>
    IReadOnlyList<PlayerbotStatus> Roster();

    /// <summary>The running bot, or null when it is not running or not in the world.</summary>
    ScenarioBotProbe? Probe(Guid botId);

    /// <summary>How many more bots may be created and run now (registry room and running room), 0 or less for none.</summary>
    int CreateRoom();

    /// <summary>Create a stopped bot through the same service <c>.playerbot create</c> uses; <paramref name="done"/> gets the result on the world thread.</summary>
    void BeginCreate(string name, byte race, byte characterClass, Action<PlayerbotOperationResult> done);

    /// <summary>Start a registered bot; <paramref name="done"/> gets the result on the world thread.</summary>
    void BeginStart(Guid botId, Action<PlayerbotOperationResult> done);

    /// <summary>Boost a running bot to <paramref name="level"/> (<c>CharacterBoost</c>); null on success, otherwise the refusal code.</summary>
    string? Boost(Guid botId, int level);

    /// <summary>Ring placement around <paramref name="anchor"/> for up to <paramref name="count"/> bots (the slots <c>.playerbot send</c> would use).</summary>
    IReadOnlyList<FleetSlot> Ring(uint map, Vector3 anchor, float orientation, int count);

    /// <summary>Teleport a running bot (no GM session); null when the teleport was accepted, otherwise the refusal code.</summary>
    string? Teleport(Guid botId, uint map, Vector3 position, float orientation);

    /// <summary>Give a running bot a defend post, or take its post away (null); null on success, otherwise the refusal code.</summary>
    string? SetDefend(Guid botId, PlayerbotDefendPost? post);

    /// <summary>The id of the siege under way, or null.</summary>
    int? RunningSiegeId { get; }

    /// <summary>How the last siege ended (<c>won</c> or <c>stopped: reason</c>), null when none has.</summary>
    string? SiegeOutcome { get; }

    /// <summary>Replace the siege waypoints with the group's and start the siege with the bots.</summary>
    ScenarioSiegeResult StartSiege(IReadOnlyList<Guid> botIds, EventScenarioSiege siege);

    /// <summary>End the siege under way; false when none runs.</summary>
    bool StopSiege(string reason);
}

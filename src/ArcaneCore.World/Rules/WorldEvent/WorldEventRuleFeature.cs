using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Rules;
using ArcaneCore.Game.Rules.WorldEvent;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Packets;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Rules.WorldEvent;

/// <summary>
/// Wires the <c>world_event</c> rule host into the world: a <see cref="WorldEventRuleRunner"/> listens to the game-event feature (every start
/// and stop, including the resumes after a restart) and is ticked from <see cref="WorldRuntime.WorldTick"/>. The rules come from
/// <see cref="RuleFeature.Catalog"/> read at every use, so <c>.reload rules</c> reaches running events at the next tick.
/// Thread affinity: the world thread (listener callbacks and the world tick). The evaluator's random sequence is seeded fresh per process.
/// </summary>
public sealed class WorldEventRuleFeature(RuleFeature rules, GameEventFeature events, ConditionFeature conditions, ILogger<WorldEventRuleFeature> logger) : IWorldFeature
{
    private long _clockMs;

    /// <summary>The runner (null before the feature is attached).</summary>
    public WorldEventRuleRunner? Runner { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var evaluator = new RuleEvaluator(Random.Shared)
        {
            Conditions = new ConditionTableGate(() => conditions.Current),
            Tracer = rules.Tracer,
            TraceSink = rules.TraceSink,
        };
        var runner = new WorldEventRuleRunner(() => rules.Catalog, new Control(events, world), evaluator, () => _clockMs, logger);
        Runner = runner;
        events.AddListener(runner);
        world.WorldTick += diffMs =>
        {
            _clockMs += diffMs;
            runner.Update();
        };
    }

    private sealed class Control(GameEventFeature events, WorldRuntime world) : IWorldEventControl
    {
        public bool StartEvent(ushort eventId)
        {
            if (events.Service is not { } service || !service.IsValidEvent(eventId))
            {
                return false;
            }

            if (!service.IsActiveEvent(eventId))
            {
                service.StartEvent(eventId);
            }

            return true;
        }

        public bool StopEvent(ushort eventId)
        {
            if (events.Service is not { } service || !service.IsValidEvent(eventId))
            {
                return false;
            }

            if (service.IsActiveEvent(eventId))
            {
                service.StopEvent(eventId);
            }

            return true;
        }

        public void Announce(string text) => world.BroadcastToAll(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(text));
    }
}

using ArcaneCore.World.Bots;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ArcaneCore.World.Playerbots.Control;

/// <summary>
/// The bots under <c>.botctl</c> control and the named bot groups of one playerbot feature (one per loaded playerbot pack: a hot reload
/// starts a fresh hub, and the old pack's controllers were detached with it). Thread-safe.
/// </summary>
internal sealed class BotControlHub
{
    private static readonly ConditionalWeakTable<ManagedPlayerbotFeature, BotControlHub> Hubs = new();

    private readonly ConcurrentDictionary<Guid, BotController> _controlled = new();
    private readonly ConcurrentDictionary<string, string[]> _groups = new(StringComparer.OrdinalIgnoreCase);

    private BotControlHub(ManagedPlayerbotFeature feature) => Feature = feature;

    public ManagedPlayerbotFeature Feature { get; }

    internal static BotControlHub For(ManagedPlayerbotFeature feature) => Hubs.GetValue(feature, static f => new BotControlHub(f));

    internal IReadOnlyCollection<BotController> Controlled => [.. _controlled.Values];

    internal BotController? Find(Guid botId) => _controlled.TryGetValue(botId, out BotController? controller) && !controller.IsDetached ? controller : null;

    /// <summary>
    /// Take <paramref name="botId"/> over: attach a fresh controller (a running bot), or start the bot scripted (<paramref name="start"/>, a
    /// stopped bot). Returns the controller, or the refusal code.
    /// </summary>
    internal async Task<(BotController? Controller, string? Error)> TakeAsync(Guid botId, string name, string owner, bool start, bool force)
    {
        if (Find(botId) is { } existing)
        {
            existing.Owner = owner;
            return (existing, null);
        }

        if (Feature.IsScripted(botId) && !force) return (null, "scripted-elsewhere");
        var controller = new BotController(this, botId, name, owner);
        _controlled[botId] = controller;
        bool attached;
        if (Feature.IsActive(botId))
        {
            attached = await Feature.SetControllerAsync(botId, controller).ConfigureAwait(false);
        }
        else if (start)
        {
            PlayerbotOperationResult started = await Feature.StartScriptedAsync(botId.ToString("D"), controller).ConfigureAwait(false);
            attached = started.Success;
            if (!attached)
            {
                _controlled.TryRemove(new KeyValuePair<Guid, BotController>(botId, controller));
                return (null, "start-failed: " + started.Code);
            }
        }
        else
        {
            _controlled.TryRemove(new KeyValuePair<Guid, BotController>(botId, controller));
            return (null, "not-running");
        }

        if (!attached)
        {
            _controlled.TryRemove(new KeyValuePair<Guid, BotController>(botId, controller));
            return (null, "not-running");
        }

        return (controller, null);
    }

    /// <summary>Hand the bot back to its own AI. False when it was not under <c>.botctl</c> control (or not running any more).</summary>
    internal async Task<bool> ReleaseAsync(Guid botId)
    {
        if (Find(botId) is not { } controller) return false;
        bool released = await Feature.SetControllerAsync(botId, null).ConfigureAwait(false);
        _controlled.TryRemove(new KeyValuePair<Guid, BotController>(botId, controller));
        return released;
    }

    /// <summary>The controller was detached by the feature (released, replaced, or the bot stopped).</summary>
    internal void Forget(BotController controller) => _controlled.TryRemove(new KeyValuePair<Guid, BotController>(controller.BotId, controller));

    internal void SetGroup(string name, IReadOnlyList<string> bots) => _groups[name] = [.. bots];

    internal bool RemoveGroup(string name) => _groups.TryRemove(name, out _);

    internal string[]? Group(string name) => _groups.TryGetValue(name, out string[]? bots) ? bots : null;

    internal IReadOnlyDictionary<string, string[]> Groups => _groups;
}
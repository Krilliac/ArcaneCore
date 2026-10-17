using System.Runtime.Loader;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Scripting;
using ArcaneCore.Game.Spells.Scripts;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.HotCode.Modules;

/// <summary>Where a module registers creature AI. Every registration is removed again when the module is replaced or unloaded.</summary>
public interface ICreatureAiRegistry
{
    /// <summary>Give every creature of <paramref name="entry"/> the AI <paramref name="factory"/> builds (the same slot as the server's own entry scripts; a clash with a built-in or another module rejects the module).</summary>
    void ForEntry(uint entry, Func<Creature, CreatureAI> factory);

    /// <summary>Register an AI under <paramref name="aiName"/>, selected by a creature template's <c>AIName</c> or <c>ScriptName</c> (a clash rejects the module).</summary>
    void Named(string aiName, Func<Creature, CreatureAI> factory);
}

/// <summary>
/// A public class of a hot module with a public parameterless constructor that implements this registers creature AI. Like the opcode and
/// command groups, it runs once while the module is being built (off the world thread), and the registrations go live at a tick boundary.
/// </summary>
public interface ICreatureAiGroup
{
    void Register(ICreatureAiRegistry registry);
}

/// <summary>What one module version contributes beyond opcode handlers and chat commands.</summary>
/// <param name="AiEntries">Entry scripts by creature entry.</param>
/// <param name="AiNames">Named creature AIs.</param>
/// <param name="Hooks">Global script hook objects (public classes implementing a hook interface of <c>ArcaneCore.Game.Scripting</c>).</param>
internal sealed record ModuleGameParts(
    IReadOnlyDictionary<uint, Func<Creature, CreatureAI>> AiEntries,
    IReadOnlyDictionary<string, Func<Creature, CreatureAI>> AiNames,
    IReadOnlyList<IScriptHooks> Hooks)
{
    private static readonly IReadOnlyDictionary<uint, Func<Map, InstanceData>> NoInstanceScripts = new Dictionary<uint, Func<Map, InstanceData>>();

    public static ModuleGameParts Empty { get; } = new(new Dictionary<uint, Func<Creature, CreatureAI>>(), new Dictionary<string, Func<Creature, CreatureAI>>(), []);

    /// <summary>
    /// The module's spell script instances (<see cref="SpellScriptRegistry.CreateScripts"/>). The exact instances are kept: the registry removes by
    /// reference, so a replace or unload hands these same objects back.
    /// </summary>
    public IReadOnlyList<ISpellScript> SpellScripts { get; init; } = [];

    /// <summary>The module's instance script factories by map id (<see cref="InstanceScriptRegistry.DiscoverFactories"/>).</summary>
    public IReadOnlyDictionary<uint, Func<Map, InstanceData>> InstanceScripts { get; init; } = NoInstanceScripts;

    /// <summary>The module's EventAI event handlers (<see cref="EventAiRegistry.DiscoverHandlers"/>).</summary>
    public IReadOnlyList<EventAiEventHandler> EventAiEvents { get; init; } = [];

    /// <summary>The module's EventAI action handlers (<see cref="EventAiRegistry.DiscoverHandlers"/>).</summary>
    public IReadOnlyList<EventAiActionHandler> EventAiActions { get; init; } = [];

    public bool HasCreatureAi => AiEntries.Count != 0 || AiNames.Count != 0;

    public bool HasEventAi => EventAiEvents.Count != 0 || EventAiActions.Count != 0;

    public bool IsEmpty => !HasCreatureAi && Hooks.Count == 0 && SpellScripts.Count == 0 && InstanceScripts.Count == 0 && !HasEventAi;
}

internal sealed class CollectingAiRegistry : ICreatureAiRegistry
{
    public Dictionary<uint, Func<Creature, CreatureAI>> Entries { get; } = [];

    public Dictionary<string, Func<Creature, CreatureAI>> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void ForEntry(uint entry, Func<Creature, CreatureAI> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!Entries.TryAdd(entry, factory))
        {
            throw new InvalidOperationException($"creature entry {entry} is registered twice by this module");
        }
    }

    public void Named(string aiName, Func<Creature, CreatureAI> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aiName);
        ArgumentNullException.ThrowIfNull(factory);
        if (!Names.TryAdd(aiName, factory))
        {
            throw new InvalidOperationException($"creature AI '{aiName}' is registered twice by this module");
        }
    }
}

/// <summary>
/// The game systems a module can reach beyond the opcode and command tables: the creature AI factory, the global script hooks, the spell script
/// registry, the instance script registry and the EventAI handler registry. Everything here runs on the world thread at a tick boundary
/// (<see cref="WorldCommit"/>), before any map updates, so no creature is being updated while it changes.
/// <list type="bullet">
/// <item><b>All or nothing.</b> <see cref="Validate"/> checks every part (creature AI, hooks, spell scripts, instance scripts, EventAI) before
/// anything live changes; <see cref="CommitSlots"/> records an undo step for every slot it changes, so a failure after validation (in any slot,
/// or later in the host's command table) is rolled back to exactly the state before the swap. A part that adds to a system this server does not
/// run (a null provider) rejects the module; a part that only removes from one is a no-op, so a module can always be unloaded.</item>
/// <item><b>Built-ins are never overridden.</b> A spell id, map id or EventAI type id that has a built-in script or handler rejects the module;
/// a module replaces only what its previous version registered.</item>
/// <item><b>Spell scripts</b> are looked up per hook, so the next hook of any cast runs the new script (<see cref="SpellScriptRegistry"/>).
/// <b>Instance scripts</b> apply to instance maps created afterwards; a loaded instance keeps its <see cref="InstanceData"/> (its progress).
/// <b>EventAI actions</b> apply from the next action run; <b>EventAI event handlers</b> are bound when a creature's EventAI is built, so a
/// creature built before the swap keeps the old handler until it respawns. <see cref="CountRetired"/> counts what still runs retired code.</item>
/// <item><b>Existing creatures.</b> A creature whose entry, <c>ScriptName</c> or <c>AIName</c> a module registers, or whose current AI is code of a
/// module being replaced or unloaded, gets a new AI object at once, unless it is in combat (a new AI would not have seen the fight begin): those
/// keep the old AI, are counted, and are retried every 5 seconds for up to 10 minutes. A creature still in combat after that keeps the old AI until
/// it is given a new one (respawn with a changed entry, charm end, a later swap); until then it keeps the retired module alive
/// (<see cref="ModuleHost.RetiredStillReferenced"/> stays above 0).</item>
/// <item>Script hooks are rebuilt in registration order with the module's hook objects removed and the new ones last. A hook running when the
/// swap happens cannot exist: hooks run on the world thread.</item>
/// <item>Not covered: map-specific entry AI.</item>
/// </list>
/// </summary>
public sealed class ModuleGameSeams
{
    private const int SweepAttempts = 120;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SweepStepTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<CreatureAiFactory?> _creatureAi;
    private readonly Func<ScriptHookRegistry?> _hooks;
    private readonly Func<IEnumerable<Map>> _maps;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly TimeSpan _sweepInterval;
    private readonly Func<Task>? _waitBetweenAttempts;
    private readonly Func<SpellScriptRegistry?> _spellScripts;
    private readonly Func<InstanceScriptRegistry?> _instanceScripts;
    private readonly Func<EventAiRegistry?> _eventAi;

    /// <param name="creatureAi">The live creature AI factory (null: not running).</param>
    /// <param name="hooks">The live script hook registry (null: not running).</param>
    /// <param name="maps">The loaded maps (creature AI refresh and <see cref="CountRetired"/>).</param>
    /// <param name="post">Queue work on the world thread.</param>
    /// <param name="logger">Where failures after validation go.</param>
    /// <param name="sweepInterval">How often deferred creatures are retried.</param>
    /// <param name="waitBetweenAttempts">Replaces the retry delay (tests).</param>
    /// <param name="spellScripts">The live spell script registry; omitted or returning null, a module with a spell script is rejected.</param>
    /// <param name="instanceScripts">The instance script registry the instance manager creates instance data from; omitted or null rejects a module with an instance script.</param>
    /// <param name="eventAi">The EventAI handler registry running creatures use; omitted or null rejects a module with an EventAI handler.</param>
    public ModuleGameSeams(
        Func<CreatureAiFactory?> creatureAi,
        Func<ScriptHookRegistry?> hooks,
        Func<IEnumerable<Map>> maps,
        Action<Action> post,
        ILogger logger,
        TimeSpan? sweepInterval = null,
        Func<Task>? waitBetweenAttempts = null,
        Func<SpellScriptRegistry?>? spellScripts = null,
        Func<InstanceScriptRegistry?>? instanceScripts = null,
        Func<EventAiRegistry?>? eventAi = null)
    {
        _spellScripts = spellScripts ?? (static () => null);
        _instanceScripts = instanceScripts ?? (static () => null);
        _eventAi = eventAi ?? (static () => null);
        _creatureAi = creatureAi ?? throw new ArgumentNullException(nameof(creatureAi));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sweepInterval = sweepInterval ?? SweepInterval;
        _waitBetweenAttempts = waitBetweenAttempts;
    }

    internal string? Validate(ModuleGameParts? old, ModuleGameParts? added)
    {
        old ??= ModuleGameParts.Empty;
        added ??= ModuleGameParts.Empty;
        if (old.IsEmpty && added.IsEmpty)
        {
            return null;
        }

        // A system the server no longer runs (its provider answers null, e.g. a detached feature) cannot take additions, but the removals meant
        // for it are no-ops: there is nothing left to remove them from, and refusing them would leave the module impossible to unload.
        if (old.HasCreatureAi || added.HasCreatureAi)
        {
            CreatureAiFactory? factory = _creatureAi();
            if (factory is null)
            {
                if (added.HasCreatureAi)
                {
                    return "the creature AI system is not running on this server";
                }
            }
            else if (factory.ValidateSwap([.. old.AiEntries.Keys], [.. old.AiNames.Keys], added.AiEntries.Keys, added.AiNames.Keys) is { } aiError)
            {
                return aiError;
            }
        }

        if (old.Hooks.Count != 0 || added.Hooks.Count != 0)
        {
            ScriptHookRegistry? hooks = _hooks();
            if (hooks is null)
            {
                if (added.Hooks.Count != 0)
                {
                    return "script hooks are not available on this server";
                }
            }
            else if (hooks.ValidateSwap(old.Hooks, added.Hooks) is { } hookError)
            {
                return hookError;
            }
        }

        if (old.SpellScripts.Count != 0 || added.SpellScripts.Count != 0)
        {
            SpellScriptRegistry? spells = _spellScripts();
            if (spells is null)
            {
                if (added.SpellScripts.Count != 0)
                {
                    return "spell scripts are not available on this server";
                }
            }
            else if (spells.ValidateSwap(old.SpellScripts, added.SpellScripts) is { } spellError)
            {
                return spellError;
            }
        }

        if (old.InstanceScripts.Count != 0 || added.InstanceScripts.Count != 0)
        {
            InstanceScriptRegistry? instances = _instanceScripts();
            if (instances is null)
            {
                if (added.InstanceScripts.Count != 0)
                {
                    return "instance scripts are not available on this server";
                }
            }
            else if (instances.ValidateSwap([.. old.InstanceScripts.Keys], added.InstanceScripts.Keys) is { } instanceError)
            {
                return instanceError;
            }
        }

        if (old.HasEventAi || added.HasEventAi)
        {
            EventAiRegistry? eventAi = _eventAi();
            if (eventAi is null)
            {
                if (added.HasEventAi)
                {
                    return "EventAI handlers are not available on this server";
                }
            }
            else if (eventAi.ValidateSwap(
                    [.. old.EventAiEvents.Select(h => h.EventType)],
                    [.. old.EventAiActions.Select(h => h.ActionType)],
                    added.EventAiEvents.Select(h => h.EventType),
                    added.EventAiActions.Select(h => h.ActionType)) is { } eventAiError)
            {
                return eventAiError;
            }
        }

        return null;
    }

    /// <summary>
    /// Apply the registry part of what <see cref="Validate"/> accepted, slot by slot (creature AI, script hooks, spell scripts, instance scripts,
    /// EventAI), recording in <paramref name="undo"/> how to put each changed slot back exactly as it was. Throws on the first failure; the caller
    /// rolls back <paramref name="undo"/>. A slot whose system is gone and that only removes is skipped; one that adds throws. Live creatures are
    /// not touched here (<see cref="Complete"/> does that once every slot of the module, opcodes and commands included, is in).
    /// <paramref name="inject"/> (tests) is called with each slot's name right after that slot changed.
    /// </summary>
    internal void CommitSlots(ModuleGameParts? old, ModuleGameParts? added, SwapUndo undo, Action<string>? inject = null)
    {
        ArgumentNullException.ThrowIfNull(undo);
        old ??= ModuleGameParts.Empty;
        added ??= ModuleGameParts.Empty;

        if ((old.HasCreatureAi || added.HasCreatureAi) && Required(_creatureAi(), added.HasCreatureAi, "the creature AI system") is { } factory)
        {
            factory.CommitSwap([.. old.AiEntries.Keys], [.. old.AiNames.Keys], added.AiEntries, added.AiNames);
            undo.Add(SwapSlots.CreatureAi, () => factory.CommitSwap([.. added.AiEntries.Keys], [.. added.AiNames.Keys], old.AiEntries, old.AiNames));
        }

        inject?.Invoke(SwapSlots.CreatureAi);

        if ((old.Hooks.Count != 0 || added.Hooks.Count != 0) && Required(_hooks(), added.Hooks.Count != 0, "the script hook registry") is { } hooks)
        {
            IScriptHooks[] before = [.. hooks.Registered];
            hooks.CommitSwap(old.Hooks, added.Hooks);

            // Every hook object out, the saved list back in: the same objects in the same registration order as before the swap.
            undo.Add(SwapSlots.ScriptHooks, () => hooks.CommitSwap([.. hooks.Registered], before));
        }

        inject?.Invoke(SwapSlots.ScriptHooks);

        if ((old.SpellScripts.Count != 0 || added.SpellScripts.Count != 0)
            && Required(_spellScripts(), added.SpellScripts.Count != 0, "the spell script registry") is { } spells)
        {
            SpellScriptRegistry.Checkpoint saved = spells.Save();
            spells.CommitSwap(old.SpellScripts, added.SpellScripts); // undoes itself and throws when a ScriptsChanged handler fails
            undo.Add(SwapSlots.SpellScripts, () => spells.Restore(saved));
        }

        inject?.Invoke(SwapSlots.SpellScripts);

        if ((old.InstanceScripts.Count != 0 || added.InstanceScripts.Count != 0)
            && Required(_instanceScripts(), added.InstanceScripts.Count != 0, "the instance script registry") is { } instances)
        {
            InstanceScriptRegistry.Checkpoint saved = instances.Save();
            instances.CommitSwap([.. old.InstanceScripts.Keys], added.InstanceScripts);
            undo.Add(SwapSlots.InstanceScripts, () => instances.Restore(saved));
        }

        inject?.Invoke(SwapSlots.InstanceScripts);

        if ((old.HasEventAi || added.HasEventAi) && Required(_eventAi(), added.HasEventAi, "the EventAI registry") is { } eventAi)
        {
            EventAiRegistry.Checkpoint saved = eventAi.Save();
            eventAi.CommitSwap(
                [.. old.EventAiEvents.Select(h => h.EventType)],
                [.. old.EventAiActions.Select(h => h.ActionType)],
                added.EventAiEvents,
                added.EventAiActions);
            undo.Add(SwapSlots.EventAi, () => eventAi.Restore(saved));
        }

        inject?.Invoke(SwapSlots.EventAi);
    }

    /// <summary>The registry, or null when it is gone and the slot only removes; throws when it is gone and the slot adds.</summary>
    private static T? Required<T>(T? registry, bool adds, string what)
        where T : class
        => registry is null && adds ? throw new InvalidOperationException($"{what} is no longer available on this server") : registry;

    /// <summary>
    /// After every slot of the swap committed: give live creatures the new AI (or the retry sweep for those in combat). Never throws; a failure is
    /// logged and only means some creatures keep their previous AI until they are rebuilt.
    /// </summary>
    internal ModuleGameCommit Complete(ModuleGameParts? old, ModuleGameParts? added, AssemblyLoadContext? oldContext)
    {
        old ??= ModuleGameParts.Empty;
        added ??= ModuleGameParts.Empty;
        if (old.IsEmpty && added.IsEmpty)
        {
            return default;
        }

        HashSet<uint> entries = [.. old.AiEntries.Keys, .. added.AiEntries.Keys];
        HashSet<string> names = new([.. old.AiNames.Keys, .. added.AiNames.Keys], StringComparer.OrdinalIgnoreCase);
        var retired = oldContext is null ? null : new WeakReference<AssemblyLoadContext>(oldContext);
        if (entries.Count == 0 && names.Count == 0 && retired is null)
        {
            return new ModuleGameCommit(0, 0, old.Hooks.Count + added.Hooks.Count);
        }

        try
        {
            HashSet<ObjectGuid> pending = [];
            (int changed, int deferred) = Refresh(entries, names, retired, null, pending);
            if (deferred > 0)
            {
                _ = SweepAsync(entries, names, retired, pending);
            }

            return new ModuleGameCommit(changed, deferred, old.Hooks.Count + added.Hooks.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "giving live creatures the swapped module AI failed; creatures not yet switched keep their previous AI until rebuilt");
            return new ModuleGameCommit(0, 0, old.Hooks.Count + added.Hooks.Count);
        }
    }

    /// <summary>
    /// World thread: the live game objects that still run code a module swap retired. These keep a replaced or unloaded module's load context
    /// alive (<see cref="ModuleHost.RetiredStillReferenced"/>) until they end; this says which kind holds it.
    /// <list type="bullet">
    /// <item><see cref="ModuleRetiredGame.InstanceMaps"/>: loaded instance maps whose <see cref="InstanceData"/> does not come from the current
    /// script of its map (<see cref="InstanceScriptRegistry.RunsCurrentScript"/>); they keep it until the instance unloads.</item>
    /// <item><see cref="ModuleRetiredGame.EventAiCreatures"/>: creatures whose EventAI has a row bound to an event handler that is no longer the
    /// current one (<see cref="EventAiRegistry.IsCurrent"/>); they keep it until they respawn.</item>
    /// </list>
    /// Spell scripts have no such count: nothing caches a spell script (see <see cref="SpellScriptRegistry"/>); only work an old script
    /// scheduled itself keeps it, which no registry can see.
    /// </summary>
    public ModuleRetiredGame CountRetired()
    {
        InstanceScriptRegistry? instances = _instanceScripts();
        EventAiRegistry? eventAi = _eventAi();
        int instanceMaps = 0;
        int creatures = 0;
        foreach (Map map in _maps())
        {
            if (instances is not null && map.FindUpdater<InstanceData>() is { } data && !instances.RunsCurrentScript(data))
            {
                instanceMaps++;
            }

            if (eventAi is not null && map.FindUpdater<CreatureMapSystem>() is { } system)
            {
                foreach (Creature creature in system.Creatures)
                {
                    if (creature.AI is CreatureEventAI ai && ai.Engine.Holders.Any(h => h.Handler is { } handler && !eventAi.IsCurrent(handler)))
                    {
                        creatures++;
                    }
                }
            }
        }

        return new ModuleRetiredGame(instanceMaps, creatures);
    }

    /// <summary>One pass over the maps (world thread). <paramref name="only"/> limits it to creatures an earlier pass deferred; <paramref name="stillDeferred"/> collects the ones still in combat.</summary>
    private (int Changed, int Deferred) Refresh(
        HashSet<uint> entries,
        HashSet<string> names,
        WeakReference<AssemblyLoadContext>? retired,
        IReadOnlySet<ObjectGuid>? only,
        ISet<ObjectGuid> stillDeferred)
    {
        int changed = 0;
        int deferred = 0;
        bool IsRetired(CreatureAI ai)
            => retired is not null && retired.TryGetTarget(out AssemblyLoadContext? context)
                && ReferenceEquals(AssemblyLoadContext.GetLoadContext(ai.GetType().Assembly), context);

        foreach (Map map in _maps())
        {
            if (map.FindUpdater<CreatureMapSystem>() is { } system)
            {
                changed += system.RefreshScriptedAi(entries, names, IsRetired, out int mapDeferred, only, stillDeferred);
                deferred += mapDeferred;
            }
        }

        return (changed, deferred);
    }

    /// <summary>
    /// Retry only the creatures the first pass deferred (a creature that already switched is never rebuilt: that would wipe its AI state).
    /// Holds the retired context only weakly, so waiting never keeps a module alive. Gives up when the world thread stops answering.
    /// </summary>
    private async Task SweepAsync(HashSet<uint> entries, HashSet<string> names, WeakReference<AssemblyLoadContext>? retired, HashSet<ObjectGuid> pending)
    {
        try
        {
            for (int attempt = 0; attempt < SweepAttempts; attempt++)
            {
                await (_waitBetweenAttempts is { } wait ? wait() : Task.Delay(_sweepInterval)).ConfigureAwait(false);
                var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                int phase = 0; // 0 queued, 1 running, 2 abandoned
                HashSet<ObjectGuid> current = pending;
                _post(() =>
                {
                    if (Interlocked.CompareExchange(ref phase, 1, 0) != 0)
                    {
                        return;
                    }

                    try
                    {
                        HashSet<ObjectGuid> next = [];
                        int left = Refresh(entries, names, retired, current, next).Deferred;
                        pending = next;
                        done.SetResult(left);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        _logger.LogError(ex, "retrying the creature AI swap failed");
                        done.SetResult(0);
                    }
                });

                using var cancelTimer = new CancellationTokenSource();
                Task finished = await Task.WhenAny(done.Task, Task.Delay(SweepStepTimeout, cancelTimer.Token)).ConfigureAwait(false);
                if (finished != done.Task && Interlocked.CompareExchange(ref phase, 2, 0) == 0)
                {
                    _logger.LogWarning("The world thread did not run the creature AI retry within {Seconds}s (world stopped?); giving up on the creatures still in combat", SweepStepTimeout.TotalSeconds);
                    return;
                }

                await cancelTimer.CancelAsync().ConfigureAwait(false);
                if (await done.Task.ConfigureAwait(false) == 0)
                {
                    return;
                }
            }

            _logger.LogWarning("Some creatures kept their previous module AI: still in combat after {Minutes} minutes", SweepAttempts * _sweepInterval.TotalMinutes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "the creature AI retry sweep stopped");
        }
    }
}

/// <summary>What <see cref="ModuleGameSeams.CountRetired"/> found still running retired module code.</summary>
/// <param name="InstanceMaps">Loaded instance maps whose instance data comes from a replaced or removed instance script.</param>
/// <param name="EventAiCreatures">Creatures whose EventAI still has a row bound to a replaced or removed event handler.</param>
public readonly record struct ModuleRetiredGame(int InstanceMaps, int EventAiCreatures);

/// <summary>What <see cref="ModuleGameSeams.Commit"/> did.</summary>
/// <param name="CreaturesSwitched">Creatures that got a new AI object now.</param>
/// <param name="CreaturesDeferred">Creatures in combat that keep the old AI for now.</param>
/// <param name="HookObjects">Script hook objects removed plus added.</param>
internal readonly record struct ModuleGameCommit(int CreaturesSwitched, int CreaturesDeferred, int HookObjects);

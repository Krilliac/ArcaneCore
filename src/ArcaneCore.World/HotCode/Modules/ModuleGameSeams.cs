using System.Runtime.Loader;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Scripting;
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
    public static ModuleGameParts Empty { get; } = new(new Dictionary<uint, Func<Creature, CreatureAI>>(), new Dictionary<string, Func<Creature, CreatureAI>>(), []);

    public bool IsEmpty => AiEntries.Count == 0 && AiNames.Count == 0 && Hooks.Count == 0;
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
/// The game systems a module can reach beyond the opcode and command tables: the creature AI factory and the global script hooks. Everything
/// here runs on the world thread at a tick boundary (<see cref="WorldCommit"/>), before any map updates, so no creature is being updated while
/// it changes.
/// <list type="bullet">
/// <item><b>All or nothing.</b> <see cref="Validate"/> runs before anything live changes; <see cref="Commit"/> only does what it proved possible.</item>
/// <item><b>Existing creatures.</b> A creature whose entry, <c>ScriptName</c> or <c>AIName</c> a module registers, or whose current AI is code of a
/// module being replaced or unloaded, gets a new AI object at once, unless it is in combat (a new AI would not have seen the fight begin): those
/// keep the old AI, are counted, and are retried every 5 seconds for up to 10 minutes. A creature still in combat after that keeps the old AI until
/// it is given a new one (respawn with a changed entry, charm end, a later swap); until then it keeps the retired module alive
/// (<see cref="ModuleHost.RetiredStillReferenced"/> stays above 0).</item>
/// <item>Script hooks are rebuilt in registration order with the module's hook objects removed and the new ones last. A hook running when the
/// swap happens cannot exist: hooks run on the world thread.</item>
/// <item>Not covered: spell scripts (their registry and effect chain are built once with the spell system) and map-specific entry AI.</item>
/// </list>
/// </summary>
public sealed class ModuleGameSeams
{
    private const int SweepAttempts = 120;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    private readonly Func<CreatureAiFactory?> _creatureAi;
    private readonly Func<ScriptHookRegistry?> _hooks;
    private readonly Func<IEnumerable<Map>> _maps;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly TimeSpan _sweepInterval;

    public ModuleGameSeams(
        Func<CreatureAiFactory?> creatureAi,
        Func<ScriptHookRegistry?> hooks,
        Func<IEnumerable<Map>> maps,
        Action<Action> post,
        ILogger logger,
        TimeSpan? sweepInterval = null)
    {
        _creatureAi = creatureAi ?? throw new ArgumentNullException(nameof(creatureAi));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sweepInterval = sweepInterval ?? SweepInterval;
    }

    /// <summary>Whether the creature AI factory exists yet (the creature feature attached).</summary>
    internal bool HasCreatureAi => _creatureAi() is not null;

    internal string? Validate(ModuleGameParts? old, ModuleGameParts? added)
    {
        old ??= ModuleGameParts.Empty;
        added ??= ModuleGameParts.Empty;
        if (old.IsEmpty && added.IsEmpty)
        {
            return null;
        }

        if (!(old.AiEntries.Count == 0 && old.AiNames.Count == 0 && added.AiEntries.Count == 0 && added.AiNames.Count == 0))
        {
            CreatureAiFactory? factory = _creatureAi();
            if (factory is null)
            {
                return "the creature AI system is not running on this server";
            }

            if (factory.ValidateSwap([.. old.AiEntries.Keys], [.. old.AiNames.Keys], added.AiEntries.Keys, added.AiNames.Keys) is { } aiError)
            {
                return aiError;
            }
        }

        if (old.Hooks.Count != 0 || added.Hooks.Count != 0)
        {
            ScriptHookRegistry? hooks = _hooks();
            if (hooks is null)
            {
                return "script hooks are not available on this server";
            }

            if (hooks.ValidateSwap(old.Hooks, added.Hooks) is { } hookError)
            {
                return hookError;
            }
        }

        return null;
    }

    /// <summary>Apply what <see cref="Validate"/> accepted. Never throws.</summary>
    internal ModuleGameCommit Commit(ModuleGameParts? old, ModuleGameParts? added, AssemblyLoadContext? oldContext)
    {
        old ??= ModuleGameParts.Empty;
        added ??= ModuleGameParts.Empty;
        if (old.IsEmpty && added.IsEmpty)
        {
            return default;
        }

        try
        {
            if (old.AiEntries.Count != 0 || old.AiNames.Count != 0 || added.AiEntries.Count != 0 || added.AiNames.Count != 0)
            {
                _creatureAi()!.CommitSwap([.. old.AiEntries.Keys], [.. old.AiNames.Keys], added.AiEntries, added.AiNames);
            }

            if (old.Hooks.Count != 0 || added.Hooks.Count != 0)
            {
                _hooks()!.CommitSwap(old.Hooks, added.Hooks);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "applying a module's creature AI / script hooks failed after validation");
            return default;
        }

        HashSet<uint> entries = [.. old.AiEntries.Keys, .. added.AiEntries.Keys];
        HashSet<string> names = new([.. old.AiNames.Keys, .. added.AiNames.Keys], StringComparer.OrdinalIgnoreCase);
        var retired = oldContext is null ? null : new WeakReference<AssemblyLoadContext>(oldContext);
        if (entries.Count == 0 && names.Count == 0 && retired is null)
        {
            return new ModuleGameCommit(0, 0, old.Hooks.Count + added.Hooks.Count);
        }

        (int changed, int deferred) = Refresh(entries, names, retired);
        if (deferred > 0)
        {
            _ = SweepAsync(entries, names, retired);
        }

        return new ModuleGameCommit(changed, deferred, old.Hooks.Count + added.Hooks.Count);
    }

    private (int Changed, int Deferred) Refresh(HashSet<uint> entries, HashSet<string> names, WeakReference<AssemblyLoadContext>? retired)
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
                changed += system.RefreshScriptedAi(entries, names, IsRetired, out int mapDeferred);
                deferred += mapDeferred;
            }
        }

        return (changed, deferred);
    }

    /// <summary>Retry the creatures that were in combat. Holds the retired context only weakly, so waiting never keeps a module alive.</summary>
    private async Task SweepAsync(HashSet<uint> entries, HashSet<string> names, WeakReference<AssemblyLoadContext>? retired)
    {
        for (int attempt = 0; attempt < SweepAttempts; attempt++)
        {
            await Task.Delay(_sweepInterval).ConfigureAwait(false);
            var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _post(() =>
            {
                try
                {
                    done.SetResult(Refresh(entries, names, retired).Deferred);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogError(ex, "retrying the creature AI swap failed");
                    done.SetResult(0);
                }
            });
            if (await done.Task.ConfigureAwait(false) == 0)
            {
                return;
            }
        }

        _logger.LogWarning("Some creatures kept their previous module AI: still in combat after {Minutes} minutes", SweepAttempts * _sweepInterval.TotalMinutes);
    }
}

/// <summary>What <see cref="ModuleGameSeams.Commit"/> did.</summary>
/// <param name="CreaturesSwitched">Creatures that got a new AI object now.</param>
/// <param name="CreaturesDeferred">Creatures in combat that keep the old AI for now.</param>
/// <param name="HookObjects">Script hook objects removed plus added.</param>
internal readonly record struct ModuleGameCommit(int CreaturesSwitched, int CreaturesDeferred, int HookObjects);

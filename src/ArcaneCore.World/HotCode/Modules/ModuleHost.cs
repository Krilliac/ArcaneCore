using ArcaneCore.Kernel.Diagnostics;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Scripting;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Ops.Announce;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.HotCode.Modules;

/// <summary>One loaded module, as <c>.hotmodule list</c> shows it.</summary>
/// <param name="SpellScripts">Spell script objects the module registered (each may serve several spell ids).</param>
/// <param name="InstanceScripts">Maps the module gave an instance script.</param>
/// <param name="EventAiHandlers">EventAI event plus action handlers the module registered.</param>
public sealed record ModuleInfo(
    string Name,
    int Opcodes,
    IReadOnlyList<string> CommandRoots,
    DateTimeOffset LoadedUtc,
    string Sha256,
    int CreatureAi = 0,
    int ScriptHooks = 0,
    int SpellScripts = 0,
    int InstanceScripts = 0,
    int EventAiHandlers = 0);

/// <summary>What a load, reload or unload did.</summary>
public sealed record ModuleResult(bool Ok, string Detail, IReadOnlyList<AnnounceField>? Fields = null, bool Failed = false);

/// <summary>
/// Loads, replaces and unloads code in the running server: a module is an assembly in
/// <c>&lt;Directory&gt;/&lt;name&gt;/&lt;name&gt;.dll</c> that is loaded into its own collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>. Its public <see cref="IOpcodeHandlerGroup"/>
/// and <see cref="ICommandGroup"/> types are instantiated and swapped into the live opcode and chat
/// command tables at a tick boundary. With <see cref="ModuleGameSeams"/> a module can also contribute
/// creature AI (<see cref="ICreatureAiGroup"/>), script hooks (<see cref="IScriptHooks"/>), spell scripts
/// (<see cref="ISpellScript"/>), instance scripts (<see cref="InstanceData"/> with <see cref="InstanceScriptAttribute"/>)
/// and EventAI handlers (<see cref="EventAiEventHandler"/>, <see cref="EventAiActionHandler"/>); the last three are
/// found the way the server finds its own (any concrete class, as <see cref="SpellScriptRegistry.CreateScripts"/>,
/// <see cref="InstanceScriptRegistry.DiscoverFactories"/> and <see cref="EventAiRegistry.DiscoverHandlers"/> do).
/// Works in a Release build on a machine without the SDK.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>All or nothing.</b> A bad module (not a managed assembly, nothing to contribute, a
/// constructor that throws, an opcode or command that clashes with something the module does not own,
/// a commit the world thread does not reach in time) changes nothing. A reload builds the new version
/// completely before touching the old one and swaps old for new in one step: on any failure the old
/// version keeps serving. A failure after validation, in any slot, is rolled back slot by slot to the
/// state before the swap and reported as failed (<see cref="ModuleResult.Failed"/>).</item>
/// <item><b>A module never replaces what it does not own.</b> Built-in handlers and roots, other
/// modules' and the registry refresh's are never overwritten; a clash rejects the module. The same holds
/// for built-in spell scripts, instance scripts and EventAI handlers.</item>
/// <item><b>Unload is cooperative.</b> The module's handlers leave the tables at a tick boundary and
/// the context is released; the runtime frees it once nothing references it. A handler already
/// running, a thread or timer the module started, an event the module subscribed to on a server
/// object, or a module type stored in a server collection keeps it alive (a leak, reported by
/// <see cref="RetiredStillReferenced"/>); the host cannot stop a module's own threads.</item>
/// <item>Module code runs with the server's full trust, at load time on a thread-pool thread and
/// afterwards wherever its handlers run.</item>
/// </list>
/// </remarks>
public sealed class ModuleHost
{
    public const int MaxModules = 32;
    private const long MaxModuleBytes = 64L * 1024 * 1024;
    private const int MaxRetiredTracked = 64;

    private static readonly Regex NamePattern = new(
        @"^[A-Za-z][A-Za-z0-9_-]{0,63}(\.[A-Za-z0-9_-]{1,63})*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly HotModuleOptions _options;
    private readonly IHotCodeWorld _world;
    private readonly OpcodeTable _opcodes;
    private readonly CommandTableSource _commands;
    private readonly HotCodeAudit _audit;
    private readonly ILogger _logger;
    private readonly TimeSpan _commitTimeout;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IHotSwapReporter _reporter;
    private readonly ModuleGameSeams? _seams;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LoadedModule> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WeakReference> _retired = [];

    public ModuleHost(
        HotModuleOptions options,
        IHotCodeWorld world,
        OpcodeTable opcodes,
        CommandTableSource commands,
        HotCodeAudit audit,
        ILogger logger,
        TimeSpan? commitTimeout = null,
        Func<DateTimeOffset>? clock = null,
        IHotSwapReporter? reporter = null,
        ModuleGameSeams? seams = null)
    {
        _seams = seams;
        _reporter = reporter ?? NullHotSwapReporter.Instance;
        _options = options;
        _world = world;
        _opcodes = opcodes;
        _commands = commands;
        _audit = audit;
        _logger = logger;
        _commitTimeout = commitTimeout ?? TimeSpan.FromSeconds(10);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The modules in force now.</summary>
    public IReadOnlyList<ModuleInfo> List()
    {
        lock (_gate)
        {
            return [.. _loaded.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select(m => m.Info)];
        }
    }

    /// <summary>
    /// How many unloaded or replaced modules the runtime has not freed yet. It reaches 0 shortly after
    /// a garbage collection once nothing references the module; a count that stays above 0 means
    /// something does (a leak). Informational: the host never forces a collection itself.
    /// </summary>
    public int RetiredStillReferenced
    {
        get
        {
            lock (_gate)
            {
                _retired.RemoveAll(r => !r.IsAlive);
                return _retired.Count;
            }
        }
    }

    /// <summary>
    /// World thread only: the instance maps and EventAI creatures still running code a module replace or unload retired
    /// (<see cref="ModuleGameSeams.CountRetired"/>), i.e. which game objects keep <see cref="RetiredStillReferenced"/> above 0. Null when this
    /// host has no game seams.
    /// </summary>
    public ModuleRetiredGame? RetiredGameReferences() => _seams?.CountRetired();

    /// <summary>Load <paramref name="name"/>. <paramref name="origin"/> (who and why) goes to the hot swap announcement; null is the console.</summary>
    public Task<ModuleResult> LoadAsync(string name, HotSwapOrigin? origin = null) => RunAsync("load", name, ModuleOperation.Load, origin);

    /// <summary>Replace the loaded version of <paramref name="name"/> with the dll now on disk.</summary>
    public Task<ModuleResult> ReloadAsync(string name, HotSwapOrigin? origin = null) => RunAsync("reload", name, ModuleOperation.Reload, origin);

    /// <summary>Remove the module's registrations and release it.</summary>
    public Task<ModuleResult> UnloadAsync(string name, HotSwapOrigin? origin = null) => RunAsync("unload", name, ModuleOperation.Unload, origin);

    /// <summary>What one world-thread commit does (the fault injection seam and the logs tell them apart).</summary>
    internal enum ModuleCommit
    {
        Load,
        Replace,
        Unload,

        /// <summary>Unloading the previous version after a failed replace.</summary>
        Cleanup,

        /// <summary>Loading the last good version again after a failed replace.</summary>
        Restore,
    }

    private enum ModuleOperation
    {
        Load,
        Reload,
        Unload,
    }

    private async Task<ModuleResult> RunAsync(string verb, string name, ModuleOperation operation, HotSwapOrigin? origin)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        long started = Stopwatch.GetTimestamp();
        try
        {
            ModuleResult result;
            var followUps = new List<(string Verb, ModuleResult Result)>();
            try
            {
                result = await CoreAsync(name, operation, followUps).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogFault("hotmodule", ex, "Hot module {Name} failed unexpectedly", name);
                result = new ModuleResult(false, $"unexpected failure: {ex.GetType().Name}: {ex.Message}");
            }

            // In order: the failed operation, then what the host did about it ("failed -> unloaded -> restored").
            Record(verb, name, result);
            ReportSwap(verb, name, result, origin ?? HotSwapOrigin.Console, Stopwatch.GetElapsedTime(started));
            foreach ((string stepVerb, ModuleResult step) in followUps)
            {
                Record(stepVerb, name, step);
                ReportSwap(stepVerb, name, step, origin ?? HotSwapOrigin.Console, Stopwatch.GetElapsedTime(started));
            }

            return result;
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<ModuleResult> CoreAsync(string name, ModuleOperation operation, List<(string Verb, ModuleResult Result)> followUps)
    {
        if (!NamePattern.IsMatch(name))
        {
            return new ModuleResult(false, "a module name is letters, digits, '_', '-' and '.' separators, starting with a letter (no paths)");
        }

        LoadedModule? existing;
        lock (_gate)
        {
            _loaded.TryGetValue(name, out existing);
            if (operation == ModuleOperation.Load && existing is not null)
            {
                return new ModuleResult(false, $"module '{name}' is already loaded (use reload)");
            }

            if (operation != ModuleOperation.Load && existing is null)
            {
                return new ModuleResult(false, $"module '{name}' is not loaded");
            }

            if (operation == ModuleOperation.Load && _loaded.Count >= MaxModules)
            {
                return new ModuleResult(false, $"at most {MaxModules} modules can be loaded at once");
            }
        }

        // Everything that can fail or run module code happens here, off the world thread and before
        // anything live is touched.
        Prepared? prepared = null;
        if (operation != ModuleOperation.Unload)
        {
            try
            {
                prepared = await Task.Run(() => Prepare(name)).ConfigureAwait(false);
            }
            catch (ModuleRejectedException ex)
            {
                return new ModuleResult(false, ex.Message);
            }
        }

        ModuleCommit kind = operation switch
        {
            ModuleOperation.Load => ModuleCommit.Load,
            ModuleOperation.Reload => ModuleCommit.Replace,
            _ => ModuleCommit.Unload,
        };

        // The queued closure holds the swap, not the module (CommitAsync): the world thread may keep the last command it ran
        // reachable for a while (an unoptimized loop frame), and that must not keep an unloaded module alive.
        var swap = new PendingSwap(existing, prepared, kind);
        string? error = await CommitAsync(swap).ConfigureAwait(false);
        swap.Clear();
        if (error is not null)
        {
            prepared?.Context.Unload();
            Retire(prepared?.Context);
            if (!swap.Failed)
            {
                return new ModuleResult(false, $"{error}; the tables are unchanged");
            }

            return await AfterFailedCommitAsync(name, operation, existing, error, swap, followUps).ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (prepared is not null)
            {
                _loaded[name] = new LoadedModule(name, prepared, _clock());
            }
            else
            {
                _loaded.Remove(name);
            }
        }

        if (existing is not null)
        {
            existing.Context.Unload();
            Retire(existing.Context);
        }

        ModuleResult final = operation switch
        {
            ModuleOperation.Unload => new ModuleResult(
                true,
                $"unloaded '{name}' ({existing!.Opcodes.Count} opcode handlers, {existing.RootNames.Length} command roots removed)",
                [new("sha256", existing.Info.Sha256[..12]), new("opcodes_removed", existing.Opcodes.Count.ToString(CultureInfo.InvariantCulture)), new("roots_removed", existing.RootNames.Length.ToString(CultureInfo.InvariantCulture))]),
            _ => new ModuleResult(
                true,
                $"{(operation == ModuleOperation.Reload ? "reloaded" : "loaded")} '{name}': {prepared!.Opcodes.Count} opcode handlers, {prepared.RootNames.Length} command roots ({string.Join(", ", prepared.RootNames.Select(r => "." + r))}); sha256 {prepared.Sha256}",
                SwapFields(prepared, existing)),
        };
        return WithGameFacts(final, swap.Game);
    }

    /// <summary>
    /// A commit failed after validation and every slot was rolled back (or the rollback itself failed). Owner policy: a failed version is never
    /// left in place. A failed load leaves nothing registered; a failed unload leaves the module loaded as before; a failed replace has already
    /// unloaded the previous version too (in the same tick, <see cref="Apply"/>), and the last good version is now rebuilt from the bytes kept
    /// when it was loaded (the file on disk may have changed), checked against the allowlist again and loaded. Each step is recorded and announced
    /// (<paramref name="followUps"/>): failed, then unloaded, then restored (or restore failed, the module staying unloaded).
    /// </summary>
    private async Task<ModuleResult> AfterFailedCommitAsync(
        string name, ModuleOperation operation, LoadedModule? existing, string error, PendingSwap swap, List<(string Verb, ModuleResult Result)> followUps)
    {
        if (swap.RollbackError is not null)
        {
            return new ModuleResult(false, $"{error}; ROLLBACK INCOMPLETE, these slots could not be put back: {swap.RollbackError} (a restart restores a known state)", Failed: true);
        }

        switch (operation)
        {
            case ModuleOperation.Load:
                return new ModuleResult(false, $"load of '{name}' failed: {error}; every slot was rolled back, nothing of it is registered", Failed: true);
            case ModuleOperation.Unload:
                return new ModuleResult(false, $"unload of '{name}' failed: {error}; every slot was rolled back, the module stays loaded as before", Failed: true);
        }

        if (!swap.PreviousUnloaded)
        {
            // The previous version is live again (rolled back) but could not be unloaded either: it stays, and the operator is told.
            return new ModuleResult(false, $"replace of '{name}' failed: {error}; rolled back, but unloading the previous version also failed ({swap.CleanupError}), so it stays loaded", Failed: true);
        }

        LoadedModule previous = existing!;
        lock (_gate)
        {
            _loaded.Remove(name);
        }

        previous.Context.Unload();
        Retire(previous.Context);
        string previousSha = previous.Info.Sha256[..12];
        followUps.Add(("unload", WithGameFacts(
            new ModuleResult(true, $"unloaded '{name}' (previous version sha256 {previousSha}) after the failed replace", [new("sha256", previousSha), new("after", "failed replace")]),
            swap.Game)));

        string? restoreError;
        Prepared? restored = null;
        try
        {
            restored = await Task.Run(() => Build(name, previous.Directory, previous.Bytes)).ConfigureAwait(false);
            var restoreSwap = new PendingSwap(null, restored, ModuleCommit.Restore);
            restoreError = await CommitAsync(restoreSwap).ConfigureAwait(false);
            restoreSwap.Clear();
            if (restoreSwap.RollbackError is { } restoreRollback)
            {
                restoreError = $"{restoreError}; ROLLBACK INCOMPLETE, these slots could not be put back: {restoreRollback} (a restart restores a known state)";
            }

            if (restoreError is null)
            {
                lock (_gate)
                {
                    _loaded[name] = new LoadedModule(name, restored, _clock());
                }

                followUps.Add(("restore", WithGameFacts(
                    new ModuleResult(true, $"restored the previous version of '{name}' (sha256 {previousSha}) from the bytes kept at its load", [new("sha256", previousSha), new("after", "failed replace")]),
                    restoreSwap.Game)));
                return new ModuleResult(false, $"replace of '{name}' failed: {error} -> unloaded -> restored previous version sha256 {previousSha}", [new("restored_sha256", previousSha)], Failed: true);
            }
        }
        catch (ModuleRejectedException ex)
        {
            restoreError = ex.Message;
        }

        if (restored is not null)
        {
            restored.Context.Unload();
            Retire(restored.Context);
        }

        followUps.Add(("restore", new ModuleResult(false, $"restoring the previous version of '{name}' (sha256 {previousSha}) failed: {restoreError}", [new("sha256", previousSha)], Failed: true)));
        return new ModuleResult(false, $"replace of '{name}' failed: {error} -> unloaded -> restoring previous version sha256 {previousSha} failed too: {restoreError}; the module is not loaded", Failed: true);
    }

    /// <summary>Add what the creature part of a commit did (AI switches now and deferred, EventAI respawns queued) to a result.</summary>
    private static ModuleResult WithGameFacts(ModuleResult result, ModuleGameCommit game)
    {
        if (game.CreaturesSwitched > 0 || game.CreaturesDeferred > 0)
        {
            List<AnnounceField> fields = [.. result.Fields ?? [], new("creatures_switched", game.CreaturesSwitched.ToString(CultureInfo.InvariantCulture)), new("creatures_in_combat_deferred", game.CreaturesDeferred.ToString(CultureInfo.InvariantCulture))];
            result = result with
            {
                Detail = $"{result.Detail}; {game.CreaturesSwitched} creatures switched AI, {game.CreaturesDeferred} in combat keep the old AI for now",
                Fields = fields,
            };
        }

        if (game.EventAiRespawnsQueued > 0)
        {
            result = result with
            {
                Detail = $"{result.Detail}; {game.EventAiRespawnsQueued} EventAI creatures respawn over the next ticks to pick up the changed event handlers",
                Fields = [.. result.Fields ?? [], new("eventai_respawns_queued", game.EventAiRespawnsQueued.ToString(CultureInfo.InvariantCulture))],
            };
        }

        return result;
    }

    private static List<AnnounceField> SwapFields(Prepared prepared, LoadedModule? replaced)
    {
        var fields = new List<AnnounceField>
        {
            new("sha256", prepared.Sha256[..12]),
            new("opcodes", prepared.Opcodes.Count.ToString(CultureInfo.InvariantCulture)),
            new("roots", string.Join(' ', prepared.RootNames.Select(r => "." + r))),
        };
        ModuleGameParts game = prepared.Game;
        if (game.HasCreatureAi || game.Hooks.Count != 0)
        {
            fields.Add(new("creature_ai", $"{game.AiEntries.Count} entries, {game.AiNames.Count} named"));
            fields.Add(new("script_hooks", game.Hooks.Count.ToString(CultureInfo.InvariantCulture)));
        }

        if (game.SpellScripts.Count != 0)
        {
            fields.Add(new("spell_scripts", game.SpellScripts.Count.ToString(CultureInfo.InvariantCulture)));
        }

        if (game.SpellHandlerTypes.Count != 0)
        {
            fields.Add(new("spell_handler_modules", game.SpellHandlerTypes.Count.ToString(CultureInfo.InvariantCulture)));
        }

        if (game.InstanceScripts.Count != 0)
        {
            fields.Add(new("instance_scripts", string.Join(' ', game.InstanceScripts.Keys.Order().Select(id => id.ToString(CultureInfo.InvariantCulture)))));
        }

        if (game.HasEventAi)
        {
            fields.Add(new("eventai", $"{game.EventAiEvents.Count} events, {game.EventAiActions.Count} actions"));
        }
        if (replaced is not null)
        {
            fields.Add(new("replaced_sha256", replaced.Info.Sha256[..12]));
        }

        return fields;
    }

    private void ReportSwap(string verb, string name, ModuleResult result, HotSwapOrigin origin, TimeSpan duration)
    {
        try
        {
            _reporter.Report(new HotSwapEvent(
                HotSwapLane.Module,
                verb == "reload" ? "replace" : verb,
                name,
                result.Ok ? HotSwapResult.Applied : result.Failed ? HotSwapResult.Failed : HotSwapResult.Rejected,
                origin,
                duration,
                result.Ok ? string.Empty : result.Detail,
                result.Fields));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("hotcode.modules", ex);
            AnomalyLedger.Shared.Report("hotcode", "module-fault", name, $"verb={verb} report_error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Warning);
            _logger.LogError(ex, "could not report the module {Verb} of '{Name}'", verb, name);
        }
    }

    /// <summary>
    /// Test seam: called on the world thread with the kind of commit and each slot's name (<see cref="SwapSlots"/>), right after that slot changed
    /// (for <see cref="SwapSlots.Commands"/>, just before the command table is swapped, the last step). A throw is a failure after validation.
    /// Null in production.
    /// </summary>
    internal Action<ModuleCommit, string>? FaultInjection { get; set; }

    /// <summary>
    /// Post <paramref name="swap"/>'s commit to the world thread and wait for it (<see cref="WorldCommit"/>). A method of its own so the queued
    /// closure captures this host and the swap and nothing else. A lambda written in the caller is linked to the caller's other captured
    /// variables: the restore's closure reached the replaced version (the <c>previous</c> the rebuild captured, through the closure class that
    /// held this host), so while the world thread still held that last command the replaced version's context could not be collected
    /// (ModuleRollbackTests, 2026-10-18).
    /// </summary>
    private Task<string?> CommitAsync(PendingSwap swap) => WorldCommit.RunAsync(_world, _commitTimeout, () => Apply(swap));

    /// <summary>
    /// World thread, start of a tick: swap the old version of the module (if any) for the new one (if any). On a failure after validation every
    /// slot is rolled back (<see cref="TryCommit"/>), and for a replace the previous version is then unloaded too, in the same tick, so no
    /// version of the module is left half applied; the host rebuilds the last good version afterwards (<see cref="AfterFailedCommitAsync"/>).
    /// Live creatures are given new AI only once every slot of a commit is in.
    /// </summary>
    private string? Apply(PendingSwap swap)
    {
        (LoadedModule? old, Prepared? added) = swap.Take();
        string? error = TryCommit(old, added, swap.Kind, out bool failed, out string? rollbackError);
        if (error is null)
        {
            if (_seams is not null)
            {
                swap.Game = _seams.Complete(old?.Game, added?.Game, old?.Context);
            }

            return null;
        }

        if (!failed)
        {
            return error;
        }

        swap.Failed = true;
        swap.RollbackError = rollbackError;
        if (swap.Kind == ModuleCommit.Replace && rollbackError is null && old is not null)
        {
            string? cleanupError = TryCommit(old, null, ModuleCommit.Cleanup, out _, out string? cleanupRollback);
            if (cleanupError is null)
            {
                swap.PreviousUnloaded = true;
                if (_seams is not null)
                {
                    swap.Game = _seams.Complete(old.Game, null, old.Context);
                }
            }
            else
            {
                swap.CleanupError = cleanupRollback is null ? cleanupError : $"{cleanupError}; ROLLBACK INCOMPLETE: {cleanupRollback}";
            }
        }

        return error;
    }

    /// <summary>
    /// Validate every slot (opcodes, creature AI, script hooks, spell scripts, instance scripts, EventAI, commands) without changing anything,
    /// then commit them one by one, recording an undo step per changed slot. Null when it applied. A refusal in validation returns why with
    /// <paramref name="failedAfterValidation"/> false and nothing changed; a failure while committing rolls every changed slot back
    /// (<paramref name="rollbackError"/>: the slots that could not be put back, or null) and sets <paramref name="failedAfterValidation"/>.
    /// </summary>
    private string? TryCommit(LoadedModule? old, Prepared? added, ModuleCommit kind, out bool failedAfterValidation, out string? rollbackError)
    {
        failedAfterValidation = false;
        rollbackError = null;
        IReadOnlyCollection<WorldOpcode> removeOpcodes = old?.Opcodes ?? [];
        IReadOnlyCollection<string> removeRoots = old?.RootNames ?? [];
        IReadOnlyList<ChatCommand> addRoots = added?.Roots ?? [];

        OpcodeTable? swapped = _opcodes.TrySwap(removeOpcodes, added?.Fresh ?? new OpcodeTable(), out string? opcodeError);
        if (swapped is null)
        {
            return opcodeError;
        }

        if (_seams?.Validate(old?.Game, added?.Game) is { } gameError)
        {
            return gameError;
        }

        // The same check CommandTableSource.TryReplace makes, on the roots that remain: a clash is refused before anything changes.
        ChatCommand[] remainingRoots = [.. _commands.Current.Roots.Where(c => !removeRoots.Contains(c.Name, StringComparer.OrdinalIgnoreCase))];
        if (CommandTableSource.Validate(remainingRoots, addRoots) is { } commandError)
        {
            return commandError;
        }

        Action<ModuleCommit, string>? fault = FaultInjection;
        Action<string>? inject = fault is null ? null : slot => fault(kind, slot);
        var undo = new SwapUndo();
        try
        {
            OpcodeTable previous = _opcodes.Copy();
            _opcodes.Replace(swapped);
            undo.Add(SwapSlots.Opcodes, () => _opcodes.Replace(previous));
            inject?.Invoke(SwapSlots.Opcodes);

            _seams?.CommitSlots(old?.Game, added?.Game, undo, inject);

            // Last, and the only step whose failure is a refusal that changed nothing: no undo step is needed for it.
            inject?.Invoke(SwapSlots.Commands);
            CommandAddResult commands = _commands.TryReplace(removeRoots, addRoots);
            if (!commands.Applied)
            {
                throw new InvalidOperationException($"the command table refused the swap: {commands.Error}");
            }

            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("hotcode.modules", ex);
            AnomalyLedger.Shared.Report("hotcode", "module-fault", kind.ToString(), $"commit_error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
            IReadOnlyList<string> changed = undo.Slots;
            _logger.LogError(ex, "A hot module {Kind} failed after validation; rolling back {Slots}", kind, changed.Count == 0 ? "nothing" : string.Join(", ", changed));
            failedAfterValidation = true;
            rollbackError = undo.RollBack((slot, rollbackEx) => _logger.LogFault("hotmodule.rollback", rollbackEx, "Rolling back the {Slot} slot of a hot module swap failed", slot));
            return $"applying it failed after validation ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private void Retire(System.Runtime.Loader.AssemblyLoadContext? context)
    {
        if (context is null)
        {
            return;
        }

        lock (_gate)
        {
            _retired.RemoveAll(r => !r.IsAlive);
            _retired.Add(new WeakReference(context));
            if (_retired.Count > MaxRetiredTracked)
            {
                _retired.RemoveAt(0);
            }
        }
    }

    private void Record(string verb, string name, ModuleResult result)
    {
        if (result.Ok)
        {
            _logger.LogWarning("Hot module {Verb} '{Name}': {Detail}", verb, name, result.Detail);
        }
        else
        {
            _logger.LogError("Hot module {Verb} '{Name}' {Outcome}: {Detail}", verb, name, result.Failed ? "FAILED" : "REJECTED", result.Detail);
        }

        try
        {
            _audit.Record(result.Ok ? "module-" + verb : result.Failed ? "module-failed" : "module-rejected", $"{verb} {name}: {result.Detail}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "could not write the hot code audit log");
        }
    }

    // NoInlining: nothing of the module (assembly, types, group instances) may stay in a local of
    // a caller's frame, or the unload proof would be measuring the compiler's liveness choices.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Prepared Prepare(string name)
    {
        string root = Path.GetFullPath(_options.Directory);
        string directory = Path.GetFullPath(Path.Combine(root, name));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(directory), root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison))
        {
            throw new ModuleRejectedException($"module '{name}' does not resolve to a folder directly under the module directory");
        }

        var directoryInfo = new DirectoryInfo(directory);
        if (!directoryInfo.Exists)
        {
            throw new ModuleRejectedException($"module folder not found: {directory}");
        }

        if (directoryInfo.LinkTarget is not null)
        {
            throw new ModuleRejectedException("a module folder that is a link is refused");
        }

        var file = new FileInfo(Path.Combine(directory, name + ".dll"));
        if (!file.Exists)
        {
            throw new ModuleRejectedException($"module file not found: {file.FullName}");
        }

        if (file.LinkTarget is not null)
        {
            throw new ModuleRejectedException("a module file that is a link is refused");
        }

        if (file.Length > MaxModuleBytes)
        {
            throw new ModuleRejectedException($"module file is larger than {MaxModuleBytes / (1024 * 1024)} MB");
        }

        return Build(name, directory, File.ReadAllBytes(file.FullName));
    }

    /// <summary>
    /// Build a module version from <paramref name="bytes"/> (read from its file by <see cref="Prepare"/>, or kept from an earlier load to restore
    /// the last good version): allowlist check, a new collectible context, discovery of every slot. Off the world thread; changes nothing live.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Prepared Build(string name, string directory, byte[] bytes)
    {
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        // The hash is of the very bytes that are loaded below (no second read), so the check cannot be raced.
        AllowlistVerdict allowlist = ModuleAllowlist.Check(_options.Allowlist, sha);
        if (!allowlist.Allowed)
        {
            throw new ModuleRejectedException($"refused by the module allowlist: {allowlist.Detail}");
        }

        var context = new ModuleLoadContext(name, directory);
        try
        {
            Assembly assembly = context.LoadFromStream(new MemoryStream(bytes));
            if (!string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ModuleRejectedException($"the assembly is named '{assembly.GetName().Name}', not '{name}'");
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                FaultLedger.Shared.Record("hotcode.modules", ex);
                AnomalyLedger.Shared.Report("hotcode", "module-fault", name, $"type_load_error={ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message}", AnomalySeverity.Warning);
                throw new ModuleRejectedException("the module's types could not be loaded: " + (ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message));
            }

            var fresh = new OpcodeTable();
            var roots = new List<ChatCommand>();
            var aiRegistry = new CollectingAiRegistry();
            var hooks = new List<IScriptHooks>();
            foreach (Type type in types.Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                bool opcodeGroup = typeof(IOpcodeHandlerGroup).IsAssignableFrom(type);
                bool commandGroup = typeof(ICommandGroup).IsAssignableFrom(type);
                bool aiGroup = typeof(ICreatureAiGroup).IsAssignableFrom(type);
                bool hookObject = typeof(IScriptHooks).IsAssignableFrom(type);
                if (!opcodeGroup && !commandGroup && !aiGroup && !hookObject)
                {
                    continue;
                }

                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    throw new ModuleRejectedException($"{type.FullName} needs a public parameterless constructor");
                }

                if ((aiGroup || hookObject) && _seams is null)
                {
                    throw new ModuleRejectedException($"{type.FullName}: this server does not offer creature AI or script hooks to modules");
                }

                object instance = Activator.CreateInstance(type)!;
                if (instance is ICreatureAiGroup aiGroupInstance)
                {
                    aiGroupInstance.Register(aiRegistry);
                }

                if (instance is IScriptHooks hooksInstance)
                {
                    hooks.Add(hooksInstance);
                }

                if (instance is IOpcodeHandlerGroup opcodes)
                {
                    opcodes.Register(fresh);
                }

                if (instance is ICommandGroup commands)
                {
                    roots.AddRange(commands.Commands);
                }
            }

            ModuleGameParts game = new ModuleGameParts(aiRegistry.Entries, aiRegistry.Names, hooks) with
            {
                SpellScripts = Discover("spell scripts", () => SpellScriptRegistry.CreateScripts(assembly)),
                InstanceScripts = Discover("instance scripts", () => InstanceScriptRegistry.DiscoverFactories([assembly])),
                SpellHandlerTypes = Discover("spell handler modules", () => SpellHandlerModules.Discover(assembly)),
            };
            EventAiHandlerSet eventAi = Discover("EventAI handlers", () => EventAiRegistry.DiscoverHandlers([assembly]));
            game = game with { EventAiEvents = eventAi.Events, EventAiActions = eventAi.Actions };
            if ((game.SpellScripts.Count != 0 || game.InstanceScripts.Count != 0 || game.HasEventAi || game.SpellHandlerTypes.Count != 0) && _seams is null)
            {
                throw new ModuleRejectedException("this server does not offer spell scripts, instance scripts or EventAI handlers to modules"
                    + (game.SpellHandlerTypes.Count != 0 ? " (or spell handler modules)" : string.Empty));
            }

            if (fresh.Count == 0 && roots.Count == 0 && game.IsEmpty)
            {
                throw new ModuleRejectedException("the module has no public IOpcodeHandlerGroup, ICommandGroup, ICreatureAiGroup or script hook class, spell script, instance script or EventAI handler that registers anything");
            }

            if (CommandTableSource.Validate([], roots) is { } rootError)
            {
                throw new ModuleRejectedException(rootError);
            }

            return new Prepared(context, fresh, fresh.Opcodes, roots, [.. roots.Select(r => r.Name)], sha, game, directory, bytes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            context.Unload();
            Retire(context);
            if (ex is not ModuleRejectedException)
            {
                // A rejection is a refusal the operator is told about. Anything else (a module constructor or static initialiser that throws, a loader failure) is unexpected and keeps its stack.
                FaultLedger.Shared.Record("hotcode.modules", ex);
                AnomalyLedger.Shared.Report("hotcode", "module-fault", name, $"load_error={ex.GetType().Name}: {ex.Message}", AnomalySeverity.Error);
                _logger.LogFault("hotmodule.load", ex, "Hot module {Name} could not be loaded", name);
            }

            throw ex as ModuleRejectedException
                ?? new ModuleRejectedException($"the module could not be loaded: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Run one of the game registries' module discoveries (spell scripts, instance scripts, EventAI handlers). Their fail-closed refusals (two
    /// scripts for one map, two handlers for one type id, a script without the constructor it needs) are rejections the operator is told about;
    /// anything else (a script constructor that throws) stays unexpected and is reported as a fault by the caller.
    /// </summary>
    private static T Discover<T>(string what, Func<T> discover)
    {
        try
        {
            return discover();
        }
        catch (InvalidOperationException ex)
        {
            FaultLedger.Shared.Record("hotcode.modules", ex);
            AnomalyLedger.Shared.Report("hotcode", "module-fault", what, $"discover_error={ex.Message}", AnomalySeverity.Warning);
            throw new ModuleRejectedException($"the module's {what} are invalid: {ex.Message}");
        }
    }

    /// <summary>What a queued commit will swap; emptied once it ran (or was given up on) so the closure never pins a module.</summary>
    private sealed class PendingSwap(LoadedModule? old, Prepared? added, ModuleCommit kind)
    {
        public ModuleCommit Kind { get; } = kind;

        /// <summary>After a failed replace: the previous version was unloaded too (world thread), so the host restores it from its bytes.</summary>
        public bool PreviousUnloaded { get; set; }

        /// <summary>After a failed replace: why unloading the previous version failed (it then stays loaded).</summary>
        public string? CleanupError { get; set; }

        /// <summary>What the creature AI / script hook part of the commit did (set on the world thread, read after the commit completed).</summary>
        public ModuleGameCommit Game { get; set; }

        /// <summary>The commit failed after validation and was rolled back (set on the world thread, read after the commit completed).</summary>
        public bool Failed { get; set; }

        /// <summary>When <see cref="Failed"/>: the slots the rollback could not put back, or null when every slot was restored.</summary>
        public string? RollbackError { get; set; }

        private LoadedModule? _old = old;
        private Prepared? _added = added;

        public (LoadedModule?, Prepared?) Take()
        {
            (LoadedModule?, Prepared?) taken = (_old, _added);
            Clear();
            return taken;
        }

        public void Clear()
        {
            _old = null;
            _added = null;
        }
    }

    private sealed class ModuleRejectedException(string message) : Exception(message);

    private sealed record Prepared(
        System.Runtime.Loader.AssemblyLoadContext Context,
        OpcodeTable Fresh,
        IReadOnlyCollection<WorldOpcode> Opcodes,
        IReadOnlyList<ChatCommand> Roots,
        string[] RootNames,
        string Sha256,
        ModuleGameParts Game,
        string Directory,
        byte[] Bytes);

    /// <summary>A module version in force. Keeps its assembly bytes and folder, so a failed replace can rebuild it without the file on disk.</summary>
    private sealed class LoadedModule(string name, Prepared version, DateTimeOffset loadedUtc)
    {
        public ModuleGameParts Game { get; } = version.Game;

        public string Name { get; } = name;

        public System.Runtime.Loader.AssemblyLoadContext Context { get; } = version.Context;

        public IReadOnlyCollection<WorldOpcode> Opcodes { get; } = version.Opcodes;

        public string[] RootNames { get; } = version.RootNames;

        /// <summary>The module folder this version was loaded from (its private dependencies resolve there).</summary>
        public string Directory { get; } = version.Directory;

        /// <summary>The exact bytes this version was loaded from (its SHA-256 is <see cref="ModuleInfo.Sha256"/>).</summary>
        public byte[] Bytes { get; } = version.Bytes;

        public ModuleInfo Info { get; } = new(
            name,
            version.Opcodes.Count,
            version.RootNames,
            loadedUtc,
            version.Sha256,
            version.Game.AiEntries.Count + version.Game.AiNames.Count,
            version.Game.Hooks.Count,
            version.Game.SpellScripts.Count,
            version.Game.InstanceScripts.Count,
            version.Game.EventAiEvents.Count + version.Game.EventAiActions.Count);
    }
}

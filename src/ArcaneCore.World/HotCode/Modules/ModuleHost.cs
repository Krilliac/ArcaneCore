using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ArcaneCore.Game.Scripting;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Ops.Announce;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.HotCode.Modules;

/// <summary>One loaded module, as <c>.hotmodule list</c> shows it.</summary>
public sealed record ModuleInfo(string Name, int Opcodes, IReadOnlyList<string> CommandRoots, DateTimeOffset LoadedUtc, string Sha256, int CreatureAi = 0, int ScriptHooks = 0);

/// <summary>What a load, reload or unload did.</summary>
public sealed record ModuleResult(bool Ok, string Detail, IReadOnlyList<AnnounceField>? Fields = null);

/// <summary>
/// Loads, replaces and unloads code in the running server: a module is an assembly in
/// <c>&lt;Directory&gt;/&lt;name&gt;/&lt;name&gt;.dll</c> that is loaded into its own collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>. Its public <see cref="IOpcodeHandlerGroup"/>
/// and <see cref="ICommandGroup"/> types are instantiated and swapped into the live opcode and chat
/// command tables at a tick boundary. Works in a Release build on a machine without the SDK.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>All or nothing.</b> A bad module (not a managed assembly, nothing to contribute, a
/// constructor that throws, an opcode or command that clashes with something the module does not own,
/// a commit the world thread does not reach in time) changes nothing. A reload builds the new version
/// completely before touching the old one and swaps old for new in one step: on any failure the old
/// version keeps serving.</item>
/// <item><b>A module never replaces what it does not own.</b> Built-in handlers and roots, other
/// modules' and the registry refresh's are never overwritten; a clash rejects the module.</item>
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

    /// <summary>Load <paramref name="name"/>. <paramref name="origin"/> (who and why) goes to the hot swap announcement; null is the console.</summary>
    public Task<ModuleResult> LoadAsync(string name, HotSwapOrigin? origin = null) => RunAsync("load", name, ModuleOperation.Load, origin);

    /// <summary>Replace the loaded version of <paramref name="name"/> with the dll now on disk.</summary>
    public Task<ModuleResult> ReloadAsync(string name, HotSwapOrigin? origin = null) => RunAsync("reload", name, ModuleOperation.Reload, origin);

    /// <summary>Remove the module's registrations and release it.</summary>
    public Task<ModuleResult> UnloadAsync(string name, HotSwapOrigin? origin = null) => RunAsync("unload", name, ModuleOperation.Unload, origin);

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
            try
            {
                result = await CoreAsync(name, operation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = new ModuleResult(false, $"unexpected failure: {ex.GetType().Name}: {ex.Message}");
            }

            Record(verb, name, result);
            ReportSwap(verb, name, result, origin ?? HotSwapOrigin.Console, Stopwatch.GetElapsedTime(started));
            return result;
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<ModuleResult> CoreAsync(string name, ModuleOperation operation)
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

        // The queued closure holds the swap, not the module: the world thread may keep the last command it ran
        // reachable for a while (an unoptimized loop frame), and that must not keep an unloaded module alive.
        var swap = new PendingSwap(existing, prepared);
        string? error = await WorldCommit.RunAsync(_world, _commitTimeout, () => Apply(swap)).ConfigureAwait(false);
        swap.Clear();
        if (error is not null)
        {
            prepared?.Context.Unload();
            Retire(prepared?.Context);
            return new ModuleResult(false, $"{error}; the tables are unchanged");
        }

        lock (_gate)
        {
            if (prepared is not null)
            {
                _loaded[name] = new LoadedModule(name, prepared.Context, prepared.Opcodes, prepared.RootNames, _clock(), prepared.Sha256, prepared.Game);
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
        if (swap.Game.CreaturesSwitched > 0 || swap.Game.CreaturesDeferred > 0)
        {
            List<AnnounceField> fields = [.. final.Fields ?? [], new("creatures_switched", swap.Game.CreaturesSwitched.ToString(CultureInfo.InvariantCulture)), new("creatures_in_combat_deferred", swap.Game.CreaturesDeferred.ToString(CultureInfo.InvariantCulture))];
            final = final with
            {
                Detail = $"{final.Detail}; {swap.Game.CreaturesSwitched} creatures switched AI, {swap.Game.CreaturesDeferred} in combat keep the old AI for now",
                Fields = fields,
            };
        }

        return final;
    }

    private static List<AnnounceField> SwapFields(Prepared prepared, LoadedModule? replaced)
    {
        var fields = new List<AnnounceField>
        {
            new("sha256", prepared.Sha256[..12]),
            new("opcodes", prepared.Opcodes.Count.ToString(CultureInfo.InvariantCulture)),
            new("roots", string.Join(' ', prepared.RootNames.Select(r => "." + r))),
        };
        if (!prepared.Game.IsEmpty)
        {
            fields.Add(new("creature_ai", $"{prepared.Game.AiEntries.Count} entries, {prepared.Game.AiNames.Count} named"));
            fields.Add(new("script_hooks", prepared.Game.Hooks.Count.ToString(CultureInfo.InvariantCulture)));
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
                result.Ok ? HotSwapResult.Applied : HotSwapResult.Rejected,
                origin,
                duration,
                result.Ok ? string.Empty : result.Detail,
                result.Fields));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "could not report the module {Verb} of '{Name}'", verb, name);
        }
    }

    /// <summary>World thread, start of a tick: swap the old version of the module (if any) for the new one (if any).</summary>
    private string? Apply(PendingSwap swap)
    {
        (LoadedModule? old, Prepared? added) = swap.Take();
        IReadOnlyCollection<WorldOpcode> removeOpcodes = old?.Opcodes ?? [];
        IReadOnlyCollection<string> removeRoots = old?.RootNames ?? [];

        OpcodeTable? swapped = _opcodes.TrySwap(removeOpcodes, added?.Fresh ?? new OpcodeTable(), out string? opcodeError);
        if (swapped is null)
        {
            return opcodeError;
        }

        // Creature AI and script hooks are checked before the command table is touched, so a clash there still changes nothing.
        if (_seams?.Validate(old?.Game, added?.Game) is { } gameError)
        {
            return gameError;
        }

        CommandAddResult commands = _commands.TryReplace(removeRoots, added?.Roots ?? []);
        if (!commands.Applied)
        {
            return commands.Error;
        }

        // Both checks passed: the remaining step is a pointer flip that cannot fail.
        _opcodes.Replace(swapped);
        if (_seams is not null)
        {
            swap.Game = _seams.Commit(old?.Game, added?.Game, old?.Context);
        }

        return null;
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
            _logger.LogError("Hot module {Verb} '{Name}' REJECTED: {Detail}", verb, name, result.Detail);
        }

        try
        {
            _audit.Record(result.Ok ? "module-" + verb : "module-rejected", $"{verb} {name}: {result.Detail}");
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

        byte[] bytes = File.ReadAllBytes(file.FullName);
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

            if (fresh.Count == 0 && roots.Count == 0 && aiRegistry.Entries.Count == 0 && aiRegistry.Names.Count == 0 && hooks.Count == 0)
            {
                throw new ModuleRejectedException("the module has no public IOpcodeHandlerGroup, ICommandGroup, ICreatureAiGroup or script hook class that registers anything");
            }

            if (CommandTableSource.Validate([], roots) is { } rootError)
            {
                throw new ModuleRejectedException(rootError);
            }

            return new Prepared(context, fresh, fresh.Opcodes, roots, [.. roots.Select(r => r.Name)], sha, new ModuleGameParts(aiRegistry.Entries, aiRegistry.Names, hooks));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            context.Unload();
            Retire(context);
            throw ex as ModuleRejectedException
                ?? new ModuleRejectedException($"the module could not be loaded: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>What a queued commit will swap; emptied once it ran (or was given up on) so the closure never pins a module.</summary>
    private sealed class PendingSwap(LoadedModule? old, Prepared? added)
    {
        /// <summary>What the creature AI / script hook part of the commit did (set on the world thread, read after the commit completed).</summary>
        public ModuleGameCommit Game { get; set; }

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
        ModuleGameParts Game);

    private sealed class LoadedModule(
        string name,
        System.Runtime.Loader.AssemblyLoadContext context,
        IReadOnlyCollection<WorldOpcode> opcodes,
        string[] rootNames,
        DateTimeOffset loadedUtc,
        string sha256,
        ModuleGameParts game)
    {
        public ModuleGameParts Game { get; } = game;

        public string Name { get; } = name;

        public System.Runtime.Loader.AssemblyLoadContext Context { get; } = context;

        public IReadOnlyCollection<WorldOpcode> Opcodes { get; } = opcodes;

        public string[] RootNames { get; } = rootNames;

        public ModuleInfo Info { get; } = new(name, opcodes.Count, rootNames, loadedUtc, sha256, game.AiEntries.Count + game.AiNames.Count, game.Hooks.Count);
    }
}

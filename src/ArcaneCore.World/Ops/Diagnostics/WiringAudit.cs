using System.Globalization;
using System.Reflection;
using ArcaneCore.Kernel.Configuration.Conf;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Ops.Diagnostics;

/// <summary>One thing that is declared but not (or wrongly) wired: <paramref name="Category"/> is one of <see cref="WiringAudit"/>'s category constants.</summary>
public sealed record WiringFinding(string Category, string Subject, string Detail)
{
    public override string ToString() => $"[{Category}] {Subject}: {Detail}";
}

/// <summary>The outcome of one <see cref="WiringAudit.Run"/>: the findings, and how much was looked at (so "clean" is not "looked at nothing").</summary>
public sealed record WiringReport(
    IReadOnlyList<WiringFinding> Findings,
    int OpcodeHandlers,
    int ConfigChecksRun,
    int OptionsConsumersChecked)
{
    public bool IsClean => Findings.Count == 0;

    /// <summary>Finding counts by category, in category order, for the categories that have any.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> Counts => [.. Findings
        .GroupBy(f => f.Category, StringComparer.Ordinal)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))];

    /// <summary>What was examined, for the clean log line.</summary>
    public string Coverage => string.Create(CultureInfo.InvariantCulture,
        $"{OpcodeHandlers} opcode handlers, {ConfigChecksRun} config checks, {OptionsConsumersChecked} IOptions consumers");

    /// <summary>The one summary line the start-up log carries: clean, or the counts per category.</summary>
    public string Summary => IsClean
        ? "wiring audit: clean (" + Coverage + ")"
        : string.Create(CultureInfo.InvariantCulture,
            $"wiring audit: {Findings.Count} finding(s): {string.Join(", ", Counts.Select(c => $"{c.Key} {c.Value}"))} (.server wiring lists them)");
}

/// <summary>
/// A runtime report of what the running world daemon declares but does not wire, computed from the live service provider and the
/// catalogs. Every category is something the code can produce today:
/// <list type="bullet">
/// <item><see cref="UnhandledOpcode"/>: client opcodes that arrived with no handler (<see cref="UnhandledOpcodeTracker"/>); counts grow while the server runs.</item>
/// <item><see cref="OptionsUnbound"/>: an options type that a class asks for as <c>IOptions&lt;T&gt;</c>/<c>IOptionsSnapshot&lt;T&gt;</c>/<c>IOptionsMonitor&lt;T&gt;</c>,
/// whose section is in the .conf catalog (so operators are told they can set it), but for which the container has no
/// <c>IConfigureOptions&lt;T&gt;</c> and no <c>IOptionsChangeTokenSource&lt;T&gt;</c>: the consumer always sees the defaults. Features that
/// read their section directly from <c>IConfiguration</c> are not options consumers and are not looked at (that direction is covered by the
/// source scan in the Docs tests).</item>
/// <item><see cref="ConfigCheckNotRun"/>: a concrete <see cref="IConfigCheck"/> in the world assembly or an assembly it references that the world's
/// <c>check-config</c> / start-up validation (<see cref="OpsCli.CreateChecks"/>) does not run.</item>
/// <item><see cref="ServerOpcodeHandler"/>: a handler registered for a server-to-client opcode (<c>SMSG_*</c>), which a client never sends.</item>
/// </list>
/// Not audited, because no cheap, non-speculative signal exists: a world feature whose <c>Attach</c> threw (an exception there aborts start-up,
/// so there is no "skipped" state to observe) and optional <c>GetService</c>-only dependencies that resolved to null.
/// <para>
/// Thread affinity: any thread; it reads immutable metadata, the DI container and atomic counters, and never touches the world thread.
/// The container is queried through <see cref="IServiceProvider.GetService"/> for singletons only. Allocation: a few lists per run; not a hot path.
/// </para>
/// </summary>
public sealed class WiringAudit
{
    public const string UnhandledOpcode = "unhandled-opcode";
    public const string OptionsUnbound = "options-unbound";
    public const string ConfigCheckNotRun = "config-check-not-run";
    public const string ServerOpcodeHandler = "handler-for-server-opcode";

    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<Assembly> _assemblies;
    private readonly ConfCatalog _catalog;

    /// <param name="services">The running container (the root provider).</param>
    /// <param name="assemblies">The assemblies to examine; defaults to the world assembly and the ArcaneCore assemblies it references.</param>
    /// <param name="catalog">The .conf catalog; defaults to the embedded one.</param>
    public WiringAudit(IServiceProvider services, IEnumerable<Assembly>? assemblies = null, ConfCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _assemblies = [.. assemblies ?? DefaultAssemblies()];
        _catalog = catalog ?? ConfCatalog.Embedded;
    }

    /// <summary>The world assembly and every ArcaneCore assembly it references (loaded by name).</summary>
    public static IReadOnlyList<Assembly> DefaultAssemblies()
    {
        Assembly world = typeof(WiringAudit).Assembly;
        var list = new List<Assembly> { world };
        foreach (AssemblyName name in world.GetReferencedAssemblies().Where(n => n.Name?.StartsWith("ArcaneCore.", StringComparison.Ordinal) == true))
        {
            list.Add(Assembly.Load(name));
        }

        return list;
    }

    private readonly Lock _staticGate = new();
    private StaticResult? _static;

    private sealed record StaticResult(IReadOnlyList<WiringFinding> Findings, int Checks, int Consumers);

    /// <summary>
    /// Run the audit. The unhandled-opcode counts and the opcode handler table are read live on every call (hot modules and
    /// <c>.hotcode refresh</c> swap the table at run time; the check is a cheap pass over its keys). The config checks and the options
    /// consumers come from reflection over assemblies and the container's registrations, which do not change while the daemon runs, so
    /// they are computed on the first call and reused (<c>.server wiring</c> runs on the world thread).
    /// </summary>
    public WiringReport Run()
    {
        var findings = new List<WiringFinding>();
        UnhandledOpcodeTracker tracker = _services.GetService<UnhandledOpcodeTracker>() ?? UnhandledOpcodeTracker.Shared;
        foreach (UnhandledOpcodeCount entry in tracker.Snapshot())
        {
            findings.Add(new WiringFinding(UnhandledOpcode, entry.Opcode,
                string.Create(CultureInfo.InvariantCulture, $"{entry.Count} client packet(s) arrived with no handler and were dropped")));
        }

        int handlers = AuditHandlers(findings);

        StaticResult computed;
        lock (_staticGate)
        {
            computed = _static ??= ComputeStatic();
        }

        findings.AddRange(computed.Findings);
        return new WiringReport(findings, handlers, computed.Checks, computed.Consumers);
    }

    private StaticResult ComputeStatic()
    {
        var findings = new List<WiringFinding>();
        int checks = AuditConfigChecks(findings);
        int consumers = AuditOptions(findings);
        return new StaticResult(findings, checks, consumers);
    }

    private int AuditHandlers(List<WiringFinding> findings)
    {
        if (_services.GetService<Handlers.OpcodeTable>() is not { } table)
        {
            return 0;
        }

        IReadOnlyCollection<WorldOpcode> opcodes = table.Opcodes;
        foreach (WorldOpcode opcode in opcodes.OrderBy(o => (int)o))
        {
            string name = WorldOpcodeNames.GetName(opcode);
            if (name.StartsWith("SMSG_", StringComparison.Ordinal))
            {
                findings.Add(new WiringFinding(ServerOpcodeHandler, name, "a handler is registered for an opcode only the server sends, so it can never run"));
            }
        }

        return opcodes.Count;
    }

    private int AuditConfigChecks(List<WiringFinding> findings)
    {
        IReadOnlyList<IConfigCheck> run = OpsCli.CreateChecks();
        foreach (Type type in ConfigCheckDiscovery.NotRun(_assemblies, run))
        {
            findings.Add(new WiringFinding(ConfigCheckNotRun, type.FullName ?? type.Name,
                "implements IConfigCheck but check-config and the start-up validation never run it (no parameterless constructor and not supplied by OpsCli, or in an assembly the daemon does not scan)"));
        }

        return run.Count;
    }

    private int AuditOptions(List<WiringFinding> findings)
    {
        HashSet<string> sections = [.. _catalog.Keys.Select(k => k.Section)];
        // options type -> the classes that ask for it
        var consumersOf = new SortedDictionary<string, (Type Options, string Section, SortedSet<string> Consumers)>(StringComparer.Ordinal);
        int checkedConsumers = 0;
        foreach (Assembly assembly in _assemblies)
        {
            foreach (Type type in SafeTypes(assembly))
            {
                if (!type.IsClass || type.IsAbstract)
                {
                    continue;
                }

                foreach (ConstructorInfo constructor in type.GetConstructors())
                {
                    foreach (ParameterInfo parameter in constructor.GetParameters())
                    {
                        if (OptionsArgument(parameter.ParameterType) is not { } options || SectionOf(options) is not { } section || !sections.Contains(section))
                        {
                            continue;
                        }

                        checkedConsumers++;
                        string key = options.FullName ?? options.Name;
                        if (!consumersOf.TryGetValue(key, out var entry))
                        {
                            entry = (options, section, []);
                            consumersOf[key] = entry;
                        }

                        entry.Consumers.Add(type.Name);
                    }
                }
            }
        }

        foreach ((Type options, string section, SortedSet<string> consumers) in consumersOf.Values)
        {
            if (!HasRegistration(typeof(IConfigureOptions<>), options) && !HasRegistration(typeof(IOptionsChangeTokenSource<>), options))
            {
                findings.Add(new WiringFinding(OptionsUnbound, options.Name,
                    $"section '{section}' is documented in the .conf catalog, but nothing configures {options.Name} in the container, so {string.Join(", ", consumers)} always see the defaults"));
            }
        }

        return checkedConsumers;
    }

    private bool HasRegistration(Type serviceDefinition, Type options)
    {
        Type enumerable = typeof(IEnumerable<>).MakeGenericType(serviceDefinition.MakeGenericType(options));
        return _services.GetService(enumerable) is System.Collections.IEnumerable items && items.GetEnumerator().MoveNext();
    }

    private static Type? OptionsArgument(Type parameterType)
    {
        if (!parameterType.IsGenericType)
        {
            return null;
        }

        Type definition = parameterType.GetGenericTypeDefinition();
        return definition == typeof(IOptions<>) || definition == typeof(IOptionsSnapshot<>) || definition == typeof(IOptionsMonitor<>)
            ? parameterType.GetGenericArguments()[0]
            : null;
    }

    /// <summary>The value of the type's public <c>SectionName</c> (or <c>Section</c>) string constant, the convention the options classes declare their section with.</summary>
    internal static string? SectionOf(Type type)
    {
        foreach (string constant in new[] { "SectionName", "Section" })
        {
            FieldInfo? field = type.GetField(constant, BindingFlags.Public | BindingFlags.Static);
            if (field is { IsLiteral: true } && field.FieldType == typeof(string))
            {
                return (string?)field.GetRawConstantValue();
            }
        }

        return null;
    }

    private static Type[] SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return [.. ex.Types.OfType<Type>()];
        }
    }
}

/// <summary>
/// Runs <see cref="WiringAudit"/> once after the host has started and logs one line: Information when clean, Warning with the counts per
/// category when not. A failure of the audit itself is a Warning too (never silent, never fatal). Registered by
/// <see cref="WorldProductionServices.AddWorldProductionServices"/>, last, so it is the last hosted service to start.
/// </summary>
internal sealed class WiringAuditReporter(WiringAudit audit, IHostApplicationLifetime lifetime, ILogger<WiringAuditReporter> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Reflection over every referenced assembly: keep it off the thread that signals ApplicationStarted. Report never throws, and
        // skips the audit once the host is stopping (the container may already be going away).
        lifetime.ApplicationStarted.Register(() => _ = Task.Run(Report));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal void Report()
    {
        if (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return;
        }

        try
        {
            WiringReport report = audit.Run();
            if (report.IsClean)
            {
                logger.LogInformation("{Summary}", report.Summary);
            }
            else
            {
                logger.LogWarning("{Summary}", report.Summary);
            }
        }
        catch (Exception ex)
        {
            try
            {
                logger.LogWarning(ex, "wiring audit could not run");
            }
            catch (Exception)
            {
                // A logger torn down by a concurrent shutdown: nothing left to tell, and this runs on a discarded task.
            }
        }
    }
}

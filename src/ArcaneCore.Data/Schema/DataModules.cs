using System.Reflection;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Schema;

/// <summary>
/// A feature's slice of one logical database: its entity mappings, the schema step that
/// introduces them and the stores it registers. Every non-abstract implementation in this
/// assembly (parameterless constructor) is discovered, so features built in parallel never
/// edit the shared context or DI files (docs/integration/seams.md).
/// <para>
/// Each schema version belongs to exactly one inline step or module, and a component's
/// versions must be contiguous from 2: a gap or a duplicate fails at startup, before any
/// database is touched (ROADMAP § Persistence: fail closed). The lead allocates versions.
/// </para>
/// </summary>
public interface IDataModule
{
    /// <summary>The logical database this module extends.</summary>
    DatabaseComponent Component { get; }

    /// <summary>The schema version whose upgrade step is <see cref="SchemaChanges"/> (2 or higher).</summary>
    int SchemaVersion { get; }

    /// <summary>Additive changes (new tables/columns of this module's model) that move a database to <see cref="SchemaVersion"/>.</summary>
    IReadOnlyList<SchemaChange> SchemaChanges { get; }

    /// <summary>Map this module's entities (called from the component's context OnModelCreating).</summary>
    void ConfigureModel(ModelBuilder modelBuilder);

    /// <summary>Register this module's stores (called from the component's Add…Database).</summary>
    void AddServices(IServiceCollection services);
}

/// <summary>Discovery and schema composition for <see cref="IDataModule"/>s.</summary>
public static class DataModules
{
    /// <summary>Every module in this assembly, ordered by full type name.</summary>
    public static IReadOnlyList<IDataModule> All { get; } = Discover(typeof(DataModules).Assembly);

    /// <summary>The modules of one component.</summary>
    public static IEnumerable<IDataModule> For(DatabaseComponent component) => All.Where(m => m.Component == component);

    /// <summary>Apply the model of every module of <paramref name="component"/>.</summary>
    public static void ConfigureModel(ModelBuilder modelBuilder, DatabaseComponent component)
    {
        foreach (IDataModule module in For(component))
        {
            module.ConfigureModel(modelBuilder);
        }
    }

    /// <summary>Register the stores of every module of <paramref name="component"/>.</summary>
    public static void AddServices(IServiceCollection services, DatabaseComponent component)
    {
        foreach (IDataModule module in For(component))
        {
            module.AddServices(services);
        }
    }

    /// <summary>
    /// A component's schema definition: <paramref name="inlineSteps"/> plus one step per module
    /// (by default the discovered modules of <paramref name="component"/>). The current version is
    /// the highest step; versions must run 2, 3, … without gaps or duplicates. <paramref name="foreignLines"/> are the
    /// other lines' numberings this component migrates from (<see cref="SchemaDefinition.ForeignLines"/>).
    /// </summary>
    public static SchemaDefinition Compose(
        DatabaseComponent component,
        string name,
        IReadOnlyList<string> version1Tables,
        IReadOnlyList<SchemaStep> inlineSteps,
        IEnumerable<IDataModule>? modules = null,
        IReadOnlyList<ForeignLine>? foreignLines = null)
    {
        var steps = new List<SchemaStep>(inlineSteps);
        foreach (IDataModule module in modules ?? For(component))
        {
            if (module.Component != component)
            {
                throw new InvalidOperationException($"{module.GetType().Name} belongs to {module.Component}, not {component}");
            }

            if (module.SchemaVersion < 2)
            {
                throw new InvalidOperationException($"{module.GetType().Name}: schema version {module.SchemaVersion} is reserved (version 1 is the base schema)");
            }

            if (steps.Any(s => s.Version == module.SchemaVersion))
            {
                throw new InvalidOperationException($"{name} schema version {module.SchemaVersion} is claimed twice ({module.GetType().Name})");
            }

            steps.Add(new SchemaStep(module.SchemaVersion, module.SchemaChanges));
        }

        steps.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Version != i + 2)
            {
                throw new InvalidOperationException($"{name} schema version {i + 2} is missing (next step is {steps[i].Version})");
            }
        }

        // The loop above proved steps run 2, 3, ... without gaps, so the highest step is steps.Count + 1,
        // which is the version the definition declares current; the bootstrapper's upgrade loop relies on
        // every step being exactly one above its predecessor.
        Invariant.Assert(steps.Count == 0 || steps[^1].Version == steps.Count + 1, $"{name} composed {steps.Count} steps but the last is version {steps[^1].Version}");

        return new SchemaDefinition
        {
            Component = name,
            CurrentVersion = steps.Count + 1,
            Version1Tables = version1Tables,
            Steps = steps,
            ForeignLines = foreignLines ?? [],
        };
    }

    private static IReadOnlyList<IDataModule> Discover(Assembly assembly) => assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDataModule).IsAssignableFrom(t))
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .Select(t => (IDataModule)(Activator.CreateInstance(t)
            ?? throw new InvalidOperationException($"could not create {t.FullName}")))
        .ToArray();
}

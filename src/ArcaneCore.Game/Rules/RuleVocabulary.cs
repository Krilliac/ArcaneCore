using System.Reflection;
using ArcaneCore.Kernel.Reflection;

namespace ArcaneCore.Game.Rules;

/// <summary>
/// The rule vocabulary: every host, event, action and target, discovered by reflection (design 5.2, like <c>EventAiRegistry</c>).
/// A primitive is a non-abstract <see cref="RuleEvent"/>, <see cref="RuleAction"/> or <see cref="RuleTarget"/> class with a
/// <see cref="RulePrimitiveAttribute"/> and a parameterless constructor; a host is a non-abstract <see cref="RuleHostDefinition"/> with
/// a parameterless constructor. Types are visited in full-name order, so discovery is deterministic.
/// <para>
/// Identity is (kind, host, id). A host looks its ids up in its own entries first, then in the shared (<see cref="RuleHostKind.Any"/>)
/// ones, so a host-specific id must not repeat a shared id of the same kind. Two classes with one identity, a host id of a shared id, two
/// hosts of one kind, or a host primitive whose host has no definition in the same set fail at startup with
/// <see cref="InvalidOperationException"/> (fail closed).
/// </para>
/// <para>
/// Code modules (<see cref="WithModule(IEnumerable{Type})"/>) may add ids, never override or shadow one that exists (the EventAI rule).
/// Immutable; safe to share between threads. Lookups happen at load (the catalog compiles references to the primitive objects), never
/// during evaluation.
/// </para>
/// </summary>
public sealed class RuleVocabulary
{
    private readonly Dictionary<(RulePrimitiveKind Kind, RuleHostKind Host, short Id), RulePrimitive> _primitives;
    private readonly Dictionary<RuleHostKind, RuleHostDefinition> _hosts;

    private RuleVocabulary(Dictionary<(RulePrimitiveKind, RuleHostKind, short), RulePrimitive> primitives, Dictionary<RuleHostKind, RuleHostDefinition> hosts)
    {
        _primitives = primitives;
        _hosts = hosts;
    }

    /// <summary>The code set whose primitives are built in: the Game assembly.</summary>
    public static CodeAssemblies StartupAssemblies { get; } = CodeAssemblies.Of(typeof(RuleVocabulary).Assembly);

    /// <summary>The vocabulary of <see cref="StartupAssemblies"/>.</summary>
    public static RuleVocabulary Default { get; } = Discover(StartupAssemblies.GetLoadableTypes());

    /// <summary>The registered hosts, by kind.</summary>
    public IReadOnlyList<RuleHostDefinition> Hosts => [.. _hosts.Values.OrderBy(h => h.Kind)];

    /// <summary>Every primitive, ordered by kind, host and id.</summary>
    public IReadOnlyList<RulePrimitiveInfo> Primitives => [.. _primitives.Values.Select(p => p.Info).OrderBy(i => i.Kind).ThenBy(i => i.Host).ThenBy(i => i.Id)];

    public RuleHostDefinition? FindHost(RuleHostKind kind) => _hosts.GetValueOrDefault(kind);

    /// <summary>The host whose name is <paramref name="name"/> (ordinal, case-insensitive), or null.</summary>
    public RuleHostDefinition? FindHost(string name) => _hosts.Values.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The event <paramref name="id"/> as host <paramref name="host"/> sees it: its own, else a shared one.</summary>
    public RuleEvent? FindEvent(RuleHostKind host, short id) => Find(RulePrimitiveKind.Event, host, id) as RuleEvent;

    public RuleAction? FindAction(RuleHostKind host, short id) => Find(RulePrimitiveKind.Action, host, id) as RuleAction;

    public RuleTarget? FindTarget(RuleHostKind host, short id) => Find(RulePrimitiveKind.Target, host, id) as RuleTarget;

    /// <summary>The primitives host <paramref name="host"/> can use (its own and the shared ones), ordered by kind and id.</summary>
    public IReadOnlyList<RulePrimitiveInfo> For(RuleHostKind host)
        => [.. Primitives.Where(p => p.Host == host || p.Host == RuleHostKind.Any).OrderBy(p => p.Kind).ThenBy(p => p.Id)];

    private RulePrimitive? Find(RulePrimitiveKind kind, RuleHostKind host, short id)
        => _primitives.GetValueOrDefault((kind, host, id)) ?? (host == RuleHostKind.Any ? null : _primitives.GetValueOrDefault((kind, RuleHostKind.Any, id)));

    /// <summary>Discover the primitives and hosts declared in <paramref name="assemblies"/> (all built in).</summary>
    public static RuleVocabulary Discover(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return Discover(assemblies.Distinct().SelectMany(a => a.GetTypes()));
    }

    /// <summary>
    /// A vocabulary of exactly the primitive and host classes among <paramref name="types"/> (all built in); other types are ignored.
    /// Throws <see cref="InvalidOperationException"/> on a duplicate identity.
    /// </summary>
    public static RuleVocabulary Discover(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var primitives = new Dictionary<(RulePrimitiveKind, RuleHostKind, short), RulePrimitive>();
        var hosts = new Dictionary<RuleHostKind, RuleHostDefinition>();
        Add(primitives, hosts, types, builtIn: true);
        CheckHosts(primitives, hosts);
        return new RuleVocabulary(primitives, hosts);
    }

    /// <summary>The primitive and host classes of <paramref name="assembly"/> (what a code module hands to <see cref="WithModule(IEnumerable{Type})"/>).</summary>
    public static IReadOnlyList<Type> TypesOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return [.. assembly.GetTypes().Where(IsCandidate).OrderBy(t => t.FullName, StringComparer.Ordinal)];
    }

    /// <summary>
    /// A new vocabulary with the module primitives and hosts among <paramref name="types"/> added (this one is unchanged). A module type that
    /// repeats or shadows an identity already present throws <see cref="InvalidOperationException"/>: modules add ids, never override.
    /// </summary>
    public RuleVocabulary WithModule(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var primitives = new Dictionary<(RulePrimitiveKind, RuleHostKind, short), RulePrimitive>(_primitives);
        var hosts = new Dictionary<RuleHostKind, RuleHostDefinition>(_hosts);
        Add(primitives, hosts, types, builtIn: false);
        CheckHosts(primitives, hosts);
        return new RuleVocabulary(primitives, hosts);
    }

    private static bool IsCandidate(Type t) => t is { IsClass: true, IsAbstract: false }
        && (typeof(RulePrimitive).IsAssignableFrom(t) || typeof(RuleHostDefinition).IsAssignableFrom(t));

    private static void Add(
        Dictionary<(RulePrimitiveKind, RuleHostKind, short), RulePrimitive> primitives, Dictionary<RuleHostKind, RuleHostDefinition> hosts,
        IEnumerable<Type> types, bool builtIn)
    {
        foreach (Type type in types.Where(IsCandidate).Distinct().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new InvalidOperationException($"rule type {type.FullName} needs a parameterless constructor");
            }

            if (typeof(RuleHostDefinition).IsAssignableFrom(type))
            {
                var host = (RuleHostDefinition)Activator.CreateInstance(type)!;
                if (host.Kind == RuleHostKind.Any)
                {
                    throw new InvalidOperationException($"rule host {type.FullName} uses kind Any, which is reserved for shared primitives");
                }

                if (!hosts.TryAdd(host.Kind, host))
                {
                    throw new InvalidOperationException($"rule host {host.Kind} is defined twice ({hosts[host.Kind].GetType().Name}, {type.Name})");
                }

                continue;
            }

            RulePrimitiveAttribute attribute = type.GetCustomAttribute<RulePrimitiveAttribute>()
                ?? throw new InvalidOperationException($"rule primitive {type.FullName} has no [RulePrimitive] attribute");
            RulePrimitiveKind kind = typeof(RuleEvent).IsAssignableFrom(type) ? RulePrimitiveKind.Event
                : typeof(RuleAction).IsAssignableFrom(type) ? RulePrimitiveKind.Action
                : typeof(RuleTarget).IsAssignableFrom(type) ? RulePrimitiveKind.Target
                : throw new InvalidOperationException($"rule primitive {type.FullName} is not an event, action or target");
            RuleParamInfo[] parameters = [.. type.GetCustomAttributes<RuleParamAttribute>().OrderBy(p => p.Index)
                .Select(p => new RuleParamInfo(p.Index, p.Name, p.Kind, p.Description))];
            int max = MaxParameters(kind);
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].Index != i + 1 || parameters[i].Index > max)
                {
                    throw new InvalidOperationException($"rule primitive {type.FullName}: parameters must be numbered 1..{max} without gaps");
                }
            }

            var primitive = (RulePrimitive)Activator.CreateInstance(type)!;
            primitive.Info = new RulePrimitiveInfo(kind, attribute.Host, attribute.Id, attribute.Name, attribute.Description, parameters, type)
            {
                IsBuiltIn = builtIn,
            };
            if (attribute.Id <= 0)
            {
                throw new InvalidOperationException($"rule primitive {type.FullName} has id {attribute.Id}; ids start at 1");
            }

            if (primitives.TryGetValue((kind, attribute.Host, attribute.Id), out RulePrimitive? existing))
            {
                throw new InvalidOperationException(Duplicate(kind, attribute.Host, attribute.Id, existing, type, module: !builtIn));
            }

            // A host id that equals a shared id (or the reverse) would make the host's lookup ambiguous.
            RulePrimitive? shadowed = attribute.Host == RuleHostKind.Any
                ? primitives.Values.FirstOrDefault(p => p.Info.Kind == kind && p.Info.Id == attribute.Id)
                : primitives.GetValueOrDefault((kind, RuleHostKind.Any, attribute.Id));
            if (shadowed is not null)
            {
                throw new InvalidOperationException(
                    $"rule {kind.ToString().ToLowerInvariant()} id {attribute.Id} of {type.Name} ({attribute.Host}) collides with {shadowed.GetType().Name} ({shadowed.Info.Host})");
            }

            primitives[(kind, attribute.Host, attribute.Id)] = primitive;
        }
    }

    private static string Duplicate(RulePrimitiveKind kind, RuleHostKind host, short id, RulePrimitive existing, Type type, bool module)
        => module && existing.Info.IsBuiltIn
            ? $"rule {kind.ToString().ToLowerInvariant()} {host} {id} is built in ({existing.GetType().Name}); module type {type.Name} cannot override it"
            : $"rule {kind.ToString().ToLowerInvariant()} {host} {id} is declared twice ({existing.GetType().Name}, {type.Name})";

    private static void CheckHosts(Dictionary<(RulePrimitiveKind, RuleHostKind, short), RulePrimitive> primitives, Dictionary<RuleHostKind, RuleHostDefinition> hosts)
    {
        foreach (RulePrimitive primitive in primitives.Values)
        {
            if (primitive.Info.Host != RuleHostKind.Any && !hosts.ContainsKey(primitive.Info.Host))
            {
                throw new InvalidOperationException($"rule primitive {primitive.GetType().Name} belongs to host {primitive.Info.Host}, which has no RuleHostDefinition");
            }
        }
    }

    /// <summary>How many parameter columns a primitive kind has: 4 event, 6 action, 3 target.</summary>
    public static int MaxParameters(RulePrimitiveKind kind) => kind switch
    {
        RulePrimitiveKind.Event => 4,
        RulePrimitiveKind.Action => 6,
        _ => 3,
    };
}

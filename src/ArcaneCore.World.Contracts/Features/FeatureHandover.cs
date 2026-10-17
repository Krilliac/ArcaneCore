using System.Diagnostics.CodeAnalysis;

namespace ArcaneCore.World.Features;

/// <summary>
/// What a detaching feature carries over to its next attach: named values of core or contract types only. A value whose
/// type comes from a collectible assembly (a pack's own type) is refused, because holding it would keep the old pack
/// loaded and the new build could not read it anyway. Only the value's own type is checked; a collection of pack objects
/// typed as a core collection is not detected (a documented limit, not a guarantee). World thread only.
/// </summary>
public sealed class FeatureHandover
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    /// <summary>The contracts version the handover was written under (<see cref="Contracts.ContractsVersion.Current"/>).</summary>
    public int ContractsVersion { get; } = Contracts.ContractsVersion.Current;

    /// <summary>The names written so far.</summary>
    public IReadOnlyCollection<string> Keys => _values.Keys;

    /// <summary>How many values were written.</summary>
    public int Count => _values.Count;

    /// <summary>Write (or overwrite) <paramref name="key"/>. Throws when the value's type comes from a collectible assembly.</summary>
    public void Set<T>(string key, T value)
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        Type type = value.GetType();
        if (IsCollectible(type))
        {
            throw new ArgumentException($"a handover holds core or contract types only; {type.FullName} comes from a collectible assembly", nameof(value));
        }

        _values[key] = value;
    }

    /// <summary>Read <paramref name="key"/> as <typeparamref name="T"/>; false when it is absent or of another type.</summary>
    public bool TryGet<T>(string key, [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_values.TryGetValue(key, out object? stored) && stored is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    private static bool IsCollectible(Type type)
    {
        if (type.Assembly.IsCollectible)
        {
            return true;
        }

        if (type.HasElementType && type.GetElementType() is { } element && IsCollectible(element))
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericArguments().Any(IsCollectible);
    }
}

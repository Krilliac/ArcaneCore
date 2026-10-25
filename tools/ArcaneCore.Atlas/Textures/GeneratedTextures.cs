namespace ArcaneCore.Atlas.Textures;

/// <summary>
/// Produces the textures behind <c>gen:&lt;scheme&gt;/&lt;arguments&gt;</c> keys (terrain alpha atlases, character composites). Thread-safe
/// implementations only: requests are concurrent, and the cache keeps the result so <see cref="Generate"/> is called once per key
/// until the cache is cleared.
/// </summary>
public interface IGeneratedTextureSource
{
    /// <summary>Lower-case letters, digits and hyphens.</summary>
    string Scheme { get; }

    /// <summary>
    /// Builds the texture for <paramref name="arguments"/> (everything after <c>scheme/</c>, untrusted text). Throws
    /// <see cref="TextureException"/> for arguments it cannot serve; <see cref="InvalidDataException"/> becomes a rejection.
    /// </summary>
    AtxTexture Generate(string arguments, CancellationToken cancellationToken);
}

/// <summary>The generated-texture schemes of this host. Modules add theirs while registering; the registry is frozen after composition.</summary>
public sealed class GeneratedTextureRegistry
{
    private readonly Dictionary<string, IGeneratedTextureSource> _sources = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _frozen;

    /// <summary>The registered schemes in ordinal order.</summary>
    public IReadOnlyList<string> Schemes
    {
        get
        {
            lock (_gate)
            {
                return [.. _sources.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <exception cref="ArgumentException">The scheme is malformed or already registered.</exception>
    /// <exception cref="InvalidOperationException">The registry is frozen.</exception>
    public void Add(IGeneratedTextureSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!IsValidScheme(source.Scheme))
        {
            throw new ArgumentException($"Texture scheme '{source.Scheme}' must be lower-case letters, digits or hyphens.");
        }

        lock (_gate)
        {
            if (_frozen)
            {
                throw new InvalidOperationException("Texture schemes cannot be added after startup.");
            }

            if (!_sources.TryAdd(source.Scheme, source))
            {
                throw new ArgumentException($"Texture scheme '{source.Scheme}' is already registered.");
            }
        }
    }

    public bool TryGet(string scheme, out IGeneratedTextureSource source)
    {
        lock (_gate)
        {
            return _sources.TryGetValue(scheme, out source!);
        }
    }

    public void Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
        }
    }

    internal static bool IsValidScheme(string? scheme)
        => !string.IsNullOrEmpty(scheme) && scheme.Length <= 32 && scheme.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}

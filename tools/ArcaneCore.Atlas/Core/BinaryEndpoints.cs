namespace ArcaneCore.Atlas.Core;

/// <summary>
/// What a binary endpoint answers: a body with a format tag (served as <c>X-Atlas-Format</c>) or an error that becomes the standard
/// <c>{"error":{"code","message"}}</c> JSON with its HTTP status.
/// </summary>
public sealed class BinaryReply
{
    private BinaryReply(int status, byte[] body, string format, string errorCode, string errorMessage)
    {
        Status = status;
        Body = body;
        Format = format;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    /// <summary>200 for a body, 400 to 599 for an error.</summary>
    public int Status { get; }

    public byte[] Body { get; }

    /// <summary>Short format tag, for example <c>ATX1</c> or <c>ATG1</c>.</summary>
    public string Format { get; }

    public string ErrorCode { get; }

    public string ErrorMessage { get; }

    public bool IsOk => Status == 200;

    public static BinaryReply Ok(byte[] body, string format)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        return new BinaryReply(200, body, format, string.Empty, string.Empty);
    }

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is not 400 to 599.</exception>
    public static BinaryReply Error(int status, string code, string message)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(status, 400);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(status, 599);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new BinaryReply(status, [], string.Empty, code, message);
    }
}

/// <summary>
/// A named binary route under <c>GET /api/bin/{name}</c>. Implementations must be thread-safe (requests are concurrent), must treat
/// every query value as untrusted text, and must not throw for bad input: they answer <see cref="BinaryReply.Error"/> instead.
/// </summary>
public interface IBinaryEndpoint
{
    /// <summary>Lower-case letters, digits and hyphens, 1 to 64 characters.</summary>
    string Name { get; }

    Task<BinaryReply> HandleAsync(IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken);
}

/// <summary>The binary endpoints of this host. Modules add theirs while registering; the registry is frozen after composition.</summary>
public sealed class BinaryEndpoints
{
    private const int MaxNameLength = 64;

    private readonly Dictionary<string, IBinaryEndpoint> _endpoints = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _frozen;

    /// <summary>The registered names in ordinal order.</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_gate)
            {
                return [.. _endpoints.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <exception cref="ArgumentException">The name is not valid or is already taken.</exception>
    /// <exception cref="InvalidOperationException">The registry is frozen.</exception>
    public void Add(IBinaryEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        string name = endpoint.Name;
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || !name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
        {
            throw new ArgumentException($"Binary endpoint name '{name}' must be 1 to {MaxNameLength} lower-case letters, digits or hyphens.");
        }

        lock (_gate)
        {
            if (_frozen)
            {
                throw new InvalidOperationException("Binary endpoints cannot be added after startup.");
            }

            if (!_endpoints.TryAdd(name, endpoint))
            {
                throw new ArgumentException($"Binary endpoint '{name}' is already registered.");
            }
        }
    }

    public bool TryGet(string name, out IBinaryEndpoint endpoint)
    {
        lock (_gate)
        {
            return _endpoints.TryGetValue(name, out endpoint!);
        }
    }

    public void Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
        }
    }
}

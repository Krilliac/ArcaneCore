using System.Security.Cryptography;

namespace ArcaneCore.World.Playerbots;

/// <summary>Why <see cref="FleetResetConfirmation.TryRedeem"/> refused a confirm code.</summary>
public enum FleetResetCodeResult
{
    /// <summary>The code matches the preview and has not expired; it is spent.</summary>
    Accepted,

    /// <summary>No preview is pending (never issued, already used, or cancelled).</summary>
    NoPreview,

    /// <summary>The 60 second window has passed; the preview is gone.</summary>
    Expired,

    /// <summary>The code is wrong. The preview stays until it expires, up to <see cref="FleetResetConfirmation.MaxWrongAttempts"/> wrong tries.</summary>
    WrongCode,

    /// <summary>The preview was issued to another invoker.</summary>
    OtherInvoker,

    /// <summary>Too many wrong codes: the preview is gone.</summary>
    TooManyAttempts,
}

/// <summary>What the preview promised, bound to its code: the counts the operator saw.</summary>
public sealed record FleetResetPromise(int Characters, int Accounts);

/// <summary>
/// The two-step confirmation of <c>.playerbot fleet reset</c>: <see cref="Issue"/> stores a short random code with the counts the
/// operator was shown, and <see cref="TryRedeem"/> spends it once, for the same invoker, within <see cref="Lifetime"/>. One preview is
/// pending at a time (a new one replaces the old). The code alphabet leaves out the characters that are easy to misread.
/// <para>Thread affinity: any thread (one lock). Time comes from a <see cref="TimeProvider"/> so a test moves it without sleeping.</para>
/// </summary>
public sealed class FleetResetConfirmation(Func<TimeProvider> clock)
{
    /// <summary>How long a code is valid after it is issued.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>The wrong codes a preview survives.</summary>
    public const int MaxWrongAttempts = 3;

    /// <summary>The length of a code.</summary>
    public const int CodeLength = 6;

    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly object _gate = new();
    private Pending? _pending;

    private sealed record Pending(string Code, string Invoker, FleetResetPromise Promise, DateTimeOffset Expires)
    {
        public int Wrong { get; set; }
    }

    /// <summary>Whether a preview is pending and not yet expired (a diagnostic; it does not spend anything).</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate) return _pending is { } p && clock().GetUtcNow() < p.Expires;
        }
    }

    /// <summary>Issue a fresh code for <paramref name="invoker"/>, replacing any pending one.</summary>
    public string Issue(string invoker, FleetResetPromise promise)
    {
        ArgumentException.ThrowIfNullOrEmpty(invoker);
        ArgumentNullException.ThrowIfNull(promise);
        Span<char> code = stackalloc char[CodeLength];
        for (int i = 0; i < code.Length; i++) code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        string text = new(code);
        lock (_gate) _pending = new Pending(text, invoker, promise, clock().GetUtcNow() + Lifetime);
        return text;
    }

    /// <summary>
    /// Spend the code. On <see cref="FleetResetCodeResult.Accepted"/> the preview is gone and <paramref name="promise"/> holds what it
    /// promised. The comparison ignores case and is constant-time in the code's length.
    /// </summary>
    public FleetResetCodeResult TryRedeem(string invoker, string code, out FleetResetPromise? promise)
    {
        promise = null;
        lock (_gate)
        {
            if (_pending is not { } pending) return FleetResetCodeResult.NoPreview;
            if (clock().GetUtcNow() >= pending.Expires)
            {
                _pending = null;
                return FleetResetCodeResult.Expired;
            }

            if (!string.Equals(pending.Invoker, invoker, StringComparison.Ordinal)) return FleetResetCodeResult.OtherInvoker;
            byte[] given = System.Text.Encoding.ASCII.GetBytes(code.Trim().ToUpperInvariant());
            byte[] expected = System.Text.Encoding.ASCII.GetBytes(pending.Code);
            if (given.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(given, expected))
            {
                if (++pending.Wrong < MaxWrongAttempts) return FleetResetCodeResult.WrongCode;
                _pending = null;
                return FleetResetCodeResult.TooManyAttempts;
            }

            _pending = null;
            promise = pending.Promise;
            return FleetResetCodeResult.Accepted;
        }
    }

    /// <summary>Drop any pending preview (a reset started, or the fleet changed).</summary>
    public void Clear()
    {
        lock (_gate) _pending = null;
    }
}

using System.Security.Cryptography;
using System.Text;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>The outcome of presenting a confirm code.</summary>
internal enum ConfirmVerdict : byte
{
    /// <summary>The code matched the actor's token and the request it was issued for; the token is spent.</summary>
    Confirmed,

    /// <summary>No live token, a wrong code, or a different request (counts as one try).</summary>
    Wrong,
}

/// <summary>
/// The bulk confirm codes (docs/areas/atlas-actions.md, "Bulk confirm"): a template applied to more targets than
/// <see cref="MapFeedActionsOptions.BulkConfirmAbove"/> needs a six-character code the daemon issued for exactly that request. An actor holds
/// one token at a time (a new request replaces it); it is bound to the actor, the template, the arguments, the targets and the point, lives
/// 60 seconds, allows three tries and is spent by the run it confirmed. Time comes from the injected <see cref="TimeProvider"/>. Thread-safe.
/// </summary>
internal sealed class MapFeedActionConfirm(TimeProvider time)
{
    /// <summary>Seconds a code lives.</summary>
    public const int ExpiresInSeconds = 60;

    /// <summary>Wrong tries after which the token is dead.</summary>
    public const int MaxTries = 3;

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 6;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Token> _tokens = new(StringComparer.Ordinal);

    /// <summary>Issue a code to <paramref name="actor"/> for the request described by <paramref name="binding"/>, replacing any earlier one.</summary>
    public string Issue(string actor, string binding)
    {
        Span<char> code = stackalloc char[CodeLength];
        for (int i = 0; i < code.Length; i++)
        {
            code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        string text = new(code);
        DateTimeOffset now = time.GetUtcNow();
        lock (_gate)
        {
            if (_tokens.Count > 256)
            {
                foreach (string stale in _tokens.Where(t => t.Value.Expires <= now).Select(t => t.Key).ToList())
                {
                    _tokens.Remove(stale);
                }
            }

            _tokens[actor] = new Token(text, binding, now.AddSeconds(ExpiresInSeconds));
        }

        return text;
    }

    /// <summary>
    /// Check <paramref name="code"/> against the token of <paramref name="actor"/> for the request <paramref name="binding"/>. A match spends the
    /// token; anything else is a try, and the third wrong try (or an expired token) removes it.
    /// </summary>
    public ConfirmVerdict Check(string actor, string binding, string? code)
    {
        DateTimeOffset now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_tokens.TryGetValue(actor, out Token? token))
            {
                return ConfirmVerdict.Wrong;
            }

            if (token.Expires <= now)
            {
                _tokens.Remove(actor);
                return ConfirmVerdict.Wrong;
            }

            bool codeOk = code is { Length: CodeLength }
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code.ToUpperInvariant()), Encoding.ASCII.GetBytes(token.Code));
            bool bindingOk = string.Equals(binding, token.Binding, StringComparison.Ordinal);
            if (codeOk && bindingOk)
            {
                _tokens.Remove(actor);
                return ConfirmVerdict.Confirmed;
            }

            if (++token.Tries >= MaxTries)
            {
                _tokens.Remove(actor);
            }

            return ConfirmVerdict.Wrong;
        }
    }

    private sealed class Token(string code, string binding, DateTimeOffset expires)
    {
        public string Code { get; } = code;

        public string Binding { get; } = binding;

        public DateTimeOffset Expires { get; } = expires;

        public int Tries;
    }
}

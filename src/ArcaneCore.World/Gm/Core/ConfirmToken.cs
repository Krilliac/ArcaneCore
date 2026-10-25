using System.Security.Cryptography;
using System.Text;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Core;

/// <summary>A confirm code that was issued: the text to echo and when it stops working.</summary>
public sealed record ConfirmToken(string Code, DateTimeOffset Expires);

/// <summary>Why <see cref="ConfirmTokenStore.TryRedeem"/> accepted or refused a code.</summary>
public enum ConfirmResult
{
    /// <summary>The code matches the preview and has not expired; it is spent.</summary>
    Accepted,

    /// <summary>No preview is pending for this invoker and action (never issued, already used, or cancelled).</summary>
    NoPreview,

    /// <summary>The <see cref="ConfirmTokenStore.Lifetime"/> has passed; the preview is gone.</summary>
    Expired,

    /// <summary>The code is wrong. The preview stays until it expires, up to <see cref="ConfirmTokenStore.MaxWrongAttempts"/> wrong tries.</summary>
    WrongCode,

    /// <summary>The preview was issued for different arguments; it stays pending and no attempt is counted.</summary>
    Mismatch,

    /// <summary>Too many wrong codes: the preview is gone.</summary>
    TooManyAttempts,
}

/// <summary>
/// Confirm tokens for destructive or broad commands (plan I7), the general form of the playerbot fleet reset's code: the first call
/// <see cref="Issue"/>s a short random code bound to the invoker, the action and the arguments the operator was shown (compared in
/// lower case with blanks collapsed, see <see cref="NormalizeBinding"/>); the same invoker echoes it within <see cref="Lifetime"/> and <see cref="TryRedeem"/> spends it once. One preview is pending per
/// invoker and action (a new one replaces it); at most <see cref="MaxPending"/> are kept. The code alphabet leaves out characters
/// that are easy to misread.
/// <para>Thread affinity: any thread (one lock). Time comes from the <see cref="TimeProvider"/> so a test moves it without sleeping.</para>
/// </summary>
public sealed class ConfirmTokenStore(TimeProvider? clock = null)
{
    /// <summary>How long a code is valid after it is issued.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>The wrong codes a preview survives.</summary>
    public const int MaxWrongAttempts = 3;

    /// <summary>The length of a code.</summary>
    public const int CodeLength = 6;

    /// <summary>The most previews kept at once; beyond it the one expiring first goes.</summary>
    public const int MaxPending = 256;

    /// <summary>The word before the code on a command line ("... confirm K7QX2M").</summary>
    public const string Keyword = "confirm";

    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Invoker, string Action), Pending> _pending = [];

    private sealed class Pending(string code, string binding, DateTimeOffset expires)
    {
        public string Code { get; } = code;

        public string Binding { get; } = binding;

        public DateTimeOffset Expires { get; } = expires;

        public int Wrong { get; set; }
    }

    /// <summary>The text a code is bound to: lower case, words separated by a single space (the plan's "normalized command text").</summary>
    public static string NormalizeBinding(string text)
        => string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>How many previews are pending and not expired (a diagnostic; it spends nothing).</summary>
    public int PendingCount
    {
        get
        {
            DateTimeOffset now = _clock.GetUtcNow();
            lock (_gate) return _pending.Values.Count(p => now < p.Expires);
        }
    }

    /// <summary>
    /// Issue a fresh code for <paramref name="invoker"/> and <paramref name="action"/>, replacing any pending one.
    /// <paramref name="binding"/> is what the code promises (normally the command arguments); the redeem must present the same text.
    /// </summary>
    public ConfirmToken Issue(string invoker, string action, string binding)
    {
        ArgumentException.ThrowIfNullOrEmpty(invoker);
        ArgumentException.ThrowIfNullOrEmpty(action);
        ArgumentNullException.ThrowIfNull(binding);
        Span<char> code = stackalloc char[CodeLength];
        for (int i = 0; i < code.Length; i++) code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        string text = new(code);
        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset expires = now + Lifetime;
        lock (_gate)
        {
            foreach ((string, string) key in _pending.Where(p => now >= p.Value.Expires).Select(p => p.Key).ToArray())
            {
                _pending.Remove(key);
            }

            _pending[(invoker, action)] = new Pending(text, NormalizeBinding(binding), expires);
            while (_pending.Count > MaxPending)
            {
                _pending.Remove(_pending.MinBy(p => p.Value.Expires).Key);
            }
        }

        return new ConfirmToken(text, expires);
    }

    /// <summary>
    /// Spend the code. On <see cref="ConfirmResult.Accepted"/> the preview is gone. The comparison ignores case and surrounding
    /// blanks and is constant-time in the code's length.
    /// </summary>
    public ConfirmResult TryRedeem(string invoker, string action, string binding, string code)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (_gate)
        {
            if (!_pending.TryGetValue((invoker, action), out Pending? pending)) return ConfirmResult.NoPreview;
            if (_clock.GetUtcNow() >= pending.Expires)
            {
                _pending.Remove((invoker, action));
                return ConfirmResult.Expired;
            }

            if (!string.Equals(pending.Binding, NormalizeBinding(binding), StringComparison.Ordinal)) return ConfirmResult.Mismatch;
            byte[] given = Encoding.ASCII.GetBytes((code ?? string.Empty).Trim().ToUpperInvariant());
            byte[] expected = Encoding.ASCII.GetBytes(pending.Code);
            if (given.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(given, expected))
            {
                if (++pending.Wrong < MaxWrongAttempts) return ConfirmResult.WrongCode;
                _pending.Remove((invoker, action));
                return ConfirmResult.TooManyAttempts;
            }

            _pending.Remove((invoker, action));
            return ConfirmResult.Accepted;
        }
    }

    /// <summary>Drop the pending preview of <paramref name="invoker"/> and <paramref name="action"/>, if any.</summary>
    public void Clear(string invoker, string action)
    {
        lock (_gate) _pending.Remove((invoker, action));
    }

    /// <summary>
    /// Split a trailing <c>confirm &lt;code&gt;</c> off a command's arguments: <paramref name="rest"/> is the arguments without it (the
    /// text a preview is bound to, blanks collapsed), <paramref name="code"/> the code or null when the line carries none. A line that ends in the keyword
    /// with no code leaves <paramref name="code"/> empty (a wrong code, not a request for a new preview).
    /// </summary>
    public static void SplitCode(string args, out string rest, out string? code)
    {
        string[] words = (args ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && words[^2].Equals(Keyword, StringComparison.OrdinalIgnoreCase))
        {
            rest = string.Join(' ', words[..^2]);
            code = words[^1];
        }
        else if (words.Length >= 1 && words[^1].Equals(Keyword, StringComparison.OrdinalIgnoreCase))
        {
            rest = string.Join(' ', words[..^1]);
            code = string.Empty;
        }
        else
        {
            rest = string.Join(' ', words);
            code = null;
        }
    }

    /// <summary>The operator-facing reply for a refused redeem.</summary>
    public static string Describe(ConfirmResult result) => result switch
    {
        ConfirmResult.NoPreview => "Nothing to confirm: run the command without a code first.",
        ConfirmResult.Expired => "The confirm code expired (" + (int)Lifetime.TotalSeconds + " s): run the command again for a new one.",
        ConfirmResult.WrongCode => "Wrong confirm code.",
        ConfirmResult.Mismatch => "The confirm code was issued for different arguments: run the command again for a new one.",
        ConfirmResult.TooManyAttempts => "Too many wrong confirm codes: run the command again for a new one.",
        _ => string.Empty,
    };

    /// <summary>
    /// The two-step flow for a command handler. Without a trailing <c>confirm &lt;code&gt;</c> in <paramref name="args"/> it issues a
    /// code, replies with <paramref name="preview"/> and how to confirm, and returns false. With one it redeems it: on success it
    /// returns true with <paramref name="effectiveArgs"/> (the arguments without the code) and <paramref name="token"/> (for
    /// <see cref="DebugWrite.Record"/>); otherwise it replies why and returns false. A false return means the handler returns true
    /// ("handled") and does nothing. A host without <see cref="GmAuditFeature"/> fails closed (no token can exist).
    /// </summary>
    public static bool TryConfirm(
        CommandContext context, string action, string args, string preview, out string effectiveArgs, out string token)
    {
        ArgumentNullException.ThrowIfNull(context);
        SplitCode(args, out effectiveArgs, out string? code);
        token = string.Empty;
        if (context.Session.Services.GetService<GmAuditFeature>()?.ConfirmTokens is not { } store)
        {
            context.Fail("confirm-unavailable");
            context.Reply("Confirmation is not available on this host.");
            return false;
        }

        string invoker = GmConfirm.InvokerKey(context);
        if (code is null)
        {
            ConfirmToken issued = store.Issue(invoker, action, effectiveArgs);
            context.Fail("confirm-required");
            context.Reply(preview + "\nTo go ahead repeat the command ending in: " + Keyword + " " + issued.Code + " (valid " + (int)Lifetime.TotalSeconds + " s).");
            return false;
        }

        ConfirmResult result = store.TryRedeem(invoker, action, effectiveArgs, code);
        if (result != ConfirmResult.Accepted)
        {
            context.Fail("confirm-" + result.ToString().ToLowerInvariant());
            context.Reply(Describe(result));
            return false;
        }

        token = code.Trim().ToUpperInvariant();
        return true;
    }
}

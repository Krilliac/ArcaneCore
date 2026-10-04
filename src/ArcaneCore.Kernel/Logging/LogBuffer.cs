using System.Buffers;
using System.Diagnostics;

namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// A growable character buffer for one log line. It starts on the caller's stack (<c>stackalloc</c>) and moves to an
/// <see cref="ArrayPool{T}"/> array only when a line outgrows it, so a typical line costs no heap allocation. Lines are capped
/// at <see cref="MaxLength"/> characters (a runaway exception text cannot pin megabytes per event); the cut is marked.
/// Owned by one stack frame: never stored, never shared across threads; <see cref="Dispose"/> returns the pooled array.
/// </summary>
public ref struct LogBuffer
{
    /// <summary>Longest line kept, in characters.</summary>
    public const int MaxLength = 256 * 1024;

    private const string TruncationMarker = " ...[truncated]";

    private char[]? _rented;
    private Span<char> _chars;
    private int _pos;

    public LogBuffer(Span<char> initial)
    {
        _chars = initial;
        _rented = null;
        _pos = 0;
        Truncated = false;
    }

    /// <summary>Characters written so far.</summary>
    public int Length => _pos;

    /// <summary>True once an append was cut at <see cref="MaxLength"/>; later appends are ignored.</summary>
    public bool Truncated { get; private set; }

    /// <summary>The line written so far.</summary>
    public ReadOnlySpan<char> Written => _chars[.._pos];

    public void Append(char c)
    {
        if (Truncated)
        {
            return;
        }

        if (_pos >= MaxLength - TruncationMarker.Length)
        {
            Truncate();
            return;
        }

        if (_pos >= _chars.Length)
        {
            Grow(1);
        }

        _chars[_pos++] = c;
    }

    public void Append(scoped ReadOnlySpan<char> text)
    {
        if (Truncated || text.IsEmpty)
        {
            return;
        }

        int limit = MaxLength - TruncationMarker.Length;
        if (_pos + text.Length > limit)
        {
            text = text[..Math.Max(0, limit - _pos)];
            if (text.Length > _chars.Length - _pos)
            {
                Grow(text.Length);
            }

            text.CopyTo(_chars[_pos..]);
            _pos += text.Length;
            Truncate();
            return;
        }

        if (text.Length > _chars.Length - _pos)
        {
            Grow(text.Length);
        }

        text.CopyTo(_chars[_pos..]);
        _pos += text.Length;
    }

    /// <summary>Appends an integer in invariant decimal notation.</summary>
    public void Append(long value)
    {
        Span<char> digits = stackalloc char[20];
        bool ok = value.TryFormat(digits, out int written, default, System.Globalization.CultureInfo.InvariantCulture);
        Debug.Assert(ok, "a long always fits in 20 characters");
        Append(digits[..written]);
    }

    /// <summary>Appends <paramref name="value"/> zero-padded to <paramref name="width"/> digits (timestamps).</summary>
    public void AppendDigits(int value, int width)
    {
        Debug.Assert(value >= 0 && width is > 0 and <= 9, "AppendDigits is for small non-negative fields");
        Span<char> digits = stackalloc char[9];
        for (int i = width - 1; i >= 0; i--)
        {
            digits[i] = (char)('0' + (value % 10));
            value /= 10;
        }

        Append(digits[..width]);
    }

    /// <summary>Returns the pooled array, if any. The buffer is empty afterwards.</summary>
    public void Dispose()
    {
        char[]? rented = _rented;
        this = default;
        if (rented is not null)
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private void Truncate()
    {
        Debug.Assert(!Truncated, "Truncate runs once per line");
        Truncated = true;
        ReadOnlySpan<char> marker = TruncationMarker;
        if (marker.Length > _chars.Length - _pos)
        {
            Grow(marker.Length);
        }

        marker.CopyTo(_chars[_pos..]);
        _pos += marker.Length;
    }

    private void Grow(int additional)
    {
        int needed = _pos + additional;
        int size = Math.Max(Math.Max(_chars.Length * 2, needed), 256);
        size = Math.Min(Math.Max(size, needed), MaxLength + TruncationMarker.Length);
        char[] array = ArrayPool<char>.Shared.Rent(size);
        _chars[.._pos].CopyTo(array);
        char[]? old = _rented;
        _rented = array;
        _chars = array;
        if (old is not null)
        {
            ArrayPool<char>.Shared.Return(old);
        }
    }
}

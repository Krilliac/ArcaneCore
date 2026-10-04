using System.Runtime.CompilerServices;

namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// The interpolated-string handler of <see cref="Invariant.Assert(bool, ref InvariantMessageHandler, string, string, int)"/>
/// and <see cref="Invariant.Check(bool, ref InvariantMessageHandler, string, string, int)"/>. The compiler
/// passes the condition into the constructor (<c>InterpolatedStringHandlerArgument</c>); when it holds,
/// <c>shouldAppend</c> is false and the compiler skips every <c>Append*</c> call, so the message of a
/// passing check is never formatted and allocates nothing (<c>Invariant</c> tests assert the zero
/// allocated-byte delta). Only a failing check builds the string, through the runtime's own
/// <see cref="DefaultInterpolatedStringHandler"/> (pooled buffer, one final string).
/// <para>
/// Ownership: a stack-only value created and consumed inside one <c>Invariant</c> call; never stored.
/// </para>
/// </summary>
[InterpolatedStringHandler]
public ref struct InvariantMessageHandler
{
    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _enabled;

    public InvariantMessageHandler(int literalLength, int formattedCount, bool condition, out bool shouldAppend)
    {
        _enabled = !condition;
        shouldAppend = _enabled;
        _inner = _enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);

    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);

    public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);

    public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);

    public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);

    public void AppendFormatted(ReadOnlySpan<char> value) => _inner.AppendFormatted(value);

    public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null) => _inner.AppendFormatted(value, alignment, format);

    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);

    public void AppendFormatted(string? value, int alignment = 0, string? format = null) => _inner.AppendFormatted(value, alignment, format);

    public void AppendFormatted(object? value, int alignment = 0, string? format = null) => _inner.AppendFormatted(value, alignment, format);

    /// <summary>The message of a failed check (empty when the check passed), releasing the pooled buffer.</summary>
    internal string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
}

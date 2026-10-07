using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// Renders the active logger scopes into a per-thread character buffer. <see cref="IExternalScopeProvider.ForEachScope{TState}"/>
/// takes a generic state, which cannot be a <c>ref struct</c> such as <see cref="LogBuffer"/>, so each thread keeps one small
/// reusable buffer here. No scopes active: the callback never runs and nothing is touched. Thread affinity: the instance is
/// <c>[ThreadStatic]</c> and used only between <see cref="Collect"/> and the copy into the line, on the same thread.
/// </summary>
public sealed class ScopeText
{
    /// <summary>The scope separator of the text sinks.</summary>
    public const string Separator = " => ";

    [ThreadStatic]
    private static ScopeText? t_current;

    private static readonly Action<object?, ScopeText> AppendScope = static (scope, text) => text.Append(scope);

    private char[] _chars = new char[256];
    private int _pos;

    private ScopeText()
    {
    }

    /// <summary>Scope entries rendered since <see cref="Collect"/>.</summary>
    public int Count { get; private set; }

    /// <summary>The rendered text: <c>" => a => b"</c> (leading separator included), empty when no scope is active.</summary>
    public ReadOnlySpan<char> Written => _chars.AsSpan(0, _pos);

    /// <summary>Renders every active scope of <paramref name="scopes"/> into this thread's buffer and returns it.</summary>
    public static ScopeText Collect(IExternalScopeProvider scopes)
    {
        ScopeText text = t_current ??= new ScopeText();
        text._pos = 0;
        text.Count = 0;
        scopes.ForEachScope(AppendScope, text);
        return text;
    }

    private void Append(object? scope)
    {
        Count++;
        Write(Separator);
        switch (scope)
        {
            case null:
                Write("(null)");
                break;
            case string s:
                Write(s);
                break;
            case IReadOnlyList<KeyValuePair<string, object?>> pairs:
                for (int i = 0; i < pairs.Count; i++)
                {
                    KeyValuePair<string, object?> pair = pairs[i];
                    if (pair.Key == "{OriginalFormat}")
                    {
                        continue;
                    }

                    if (i > 0)
                    {
                        Write(", ");
                    }

                    Write(pair.Key);
                    Write("=");
                    Write(Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty);
                }

                break;
            default:
                Write(Convert.ToString(scope, CultureInfo.InvariantCulture) ?? string.Empty);
                break;
        }
    }

    private void Write(ReadOnlySpan<char> text)
    {
        if (_pos + text.Length > LogBuffer.MaxLength)
        {
            return;
        }

        if (_pos + text.Length > _chars.Length)
        {
            Array.Resize(ref _chars, Math.Max(_chars.Length * 2, _pos + text.Length));
        }

        text.CopyTo(_chars.AsSpan(_pos));
        _pos += text.Length;
    }
}

using System.Globalization;
using System.Text;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// The reply sink of one action's executor: collects the lines the command prints (from the world thread and from the threads async handlers
/// reply on) until <see cref="Finish"/>, after which a further line goes to the late-line callback (the connection sends it as an
/// <c>actionLine</c>). Both are bounded: at most <see cref="MaxLines"/> lines are kept, the rest counted and reported by one closing line, and at most
/// <see cref="MaxLateLines"/> late lines are forwarded, then one note. Thread-safe (one lock; a write takes it once however many lines it holds);
/// lives for one request.
/// </summary>
internal sealed class ActionSink(Action<string> lateLine) : TextWriter
{
    /// <summary>The most lines kept for the answer.</summary>
    public const int MaxLines = 2000;

    /// <summary>The most lines forwarded after the answer was built.</summary>
    public const int MaxLateLines = 200;

    private readonly Lock _gate = new();
    private readonly StringBuilder _partial = new();
    private readonly List<string> _lines = [];
    private bool _closed;
    private int _dropped;
    private int _lateSent;

    /// <inheritdoc />
    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>How many lines were collected so far.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _lines.Count;
            }
        }
    }

    /// <summary>The lines from index <paramref name="from"/> up to (not including) <paramref name="to"/>.</summary>
    public string[] Slice(int from, int to)
    {
        lock (_gate)
        {
            return [.. _lines.Skip(from).Take(Math.Max(0, to - from))];
        }
    }

    /// <summary>Stop collecting and return all lines so far (and, past the cap, one line saying how many were dropped); later lines go to the late-line callback.</summary>
    public string[] Finish()
    {
        lock (_gate)
        {
            if (!_closed && _dropped > 0)
            {
                _lines.Add(string.Create(CultureInfo.InvariantCulture, $"... {_dropped} more lines were dropped"));
            }

            _closed = true;
            return [.. _lines];
        }
    }

    /// <inheritdoc />
    public override void Write(char value) => Append(new ReadOnlySpan<char>(in value));

    /// <inheritdoc />
    public override void Write(string? value)
    {
        if (value is not null)
        {
            Append(value);
        }
    }

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count) => Append(new ReadOnlySpan<char>(buffer, index, count));

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<char> buffer) => Append(buffer);

    /// <summary>Split <paramref name="text"/> on newlines under one lock; a trailing part without a newline waits for its end.</summary>
    private void Append(ReadOnlySpan<char> text)
    {
        List<string>? late = null;
        lock (_gate)
        {
            while (true)
            {
                int newline = text.IndexOf('\n');
                if (newline < 0)
                {
                    _partial.Append(text);
                    break;
                }

                _partial.Append(text[..newline]);
                string line = _partial.ToString().TrimEnd('\r');
                _partial.Clear();
                AddLocked(line, ref late);
                text = text[(newline + 1)..];
            }
        }

        if (late is not null)
        {
            foreach (string line in late)
            {
                lateLine(line);
            }
        }
    }

    /// <summary>Under the lock: keep the line, count it as dropped past the cap, or queue it for the late-line callback (called outside the lock).</summary>
    private void AddLocked(string line, ref List<string>? late)
    {
        if (!_closed)
        {
            if (_lines.Count < MaxLines)
            {
                _lines.Add(line);
            }
            else
            {
                _dropped++;
            }

            return;
        }

        if (_lateSent < MaxLateLines)
        {
            _lateSent++;
            (late ??= []).Add(line);
        }
        else if (_lateSent == MaxLateLines)
        {
            _lateSent++;
            (late ??= []).Add("... further lines were dropped");
        }
    }
}

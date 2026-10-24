using System.Text;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// The reply sink of one action's executor: collects every line the command prints (from the world thread and from the threads async handlers
/// reply on) until <see cref="Finish"/>, after which a further line goes to the late-line callback (the connection sends it as an
/// <c>actionLine</c>). Thread-safe; lives for one request.
/// </summary>
internal sealed class ActionSink(Action<string> lateLine) : TextWriter
{
    private readonly Lock _gate = new();
    private readonly StringBuilder _partial = new();
    private readonly List<string> _lines = [];
    private bool _closed;

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

    /// <summary>Stop collecting and return all lines so far; later lines go to the late-line callback.</summary>
    public string[] Finish()
    {
        lock (_gate)
        {
            _closed = true;
            return [.. _lines];
        }
    }

    /// <inheritdoc />
    public override void Write(char value)
    {
        string? line = null;
        lock (_gate)
        {
            if (value == '\n')
            {
                line = _partial.ToString().TrimEnd('\r');
                _partial.Clear();
            }
            else
            {
                _partial.Append(value);
            }
        }

        if (line is not null)
        {
            Add(line);
        }
    }

    /// <inheritdoc />
    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        foreach (char c in value)
        {
            Write(c);
        }
    }

    private void Add(string line)
    {
        bool late;
        lock (_gate)
        {
            late = _closed;
            if (!late)
            {
                _lines.Add(line);
            }
        }

        if (late)
        {
            lateLine(line);
        }
    }
}

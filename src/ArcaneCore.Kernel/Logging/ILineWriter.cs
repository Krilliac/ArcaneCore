namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// Where a sink puts its finished lines. <see cref="Write"/> receives one complete line (newline included) and must copy it
/// before returning: the span is the caller's stack buffer. Thread safety is per implementation (see each class).
/// </summary>
public interface ILineWriter : IDisposable
{
    void Write(ReadOnlySpan<char> line);

    /// <summary>Pushes buffered output to its destination.</summary>
    void Flush();
}

/// <summary>
/// Synchronous writer over a <see cref="TextWriter"/> (the console, or a <see cref="StringWriter"/> in tests). Writes are
/// serialised by a lock so two threads never interleave characters of one line. Allocation-free when the underlying writer's
/// <see cref="TextWriter.Write(ReadOnlySpan{char})"/> is (a <see cref="StreamWriter"/>'s is).
/// </summary>
public sealed class TextWriterLineWriter(TextWriter writer, bool flushEachLine = false) : ILineWriter
{
    private readonly object _gate = new();

    public void Write(ReadOnlySpan<char> line)
    {
        lock (_gate)
        {
            writer.Write(line);
            if (flushEachLine)
            {
                writer.Flush();
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            writer.Flush();
        }
    }

    /// <summary>Flushes; the writer itself is owned by whoever created it (never the console).</summary>
    public void Dispose() => Flush();
}

/// <summary>Discards everything (a sink that is switched off, and allocation tests).</summary>
public sealed class NullLineWriter : ILineWriter
{
    public static NullLineWriter Instance { get; } = new();

    public void Write(ReadOnlySpan<char> line)
    {
    }

    public void Flush()
    {
    }

    public void Dispose()
    {
    }
}

using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// One destination of the provider: formats an event into a stack <see cref="LogBuffer"/> and hands the line to its
/// <see cref="ILineWriter"/>. <see cref="Enabled"/> is read with volatile semantics on every event so a reload can switch a sink
/// off without a lock. Thread affinity: <see cref="Emit{TState}"/> runs on the logging thread (often the world thread); the
/// writer decides whether that thread touches the destination (the queued writers do not).
/// </summary>
public abstract class LogSink : IDisposable
{
    private volatile bool _enabled = true;
    private long _faults;

    protected LogSink(string name, ILineWriter writer)
    {
        Name = name;
        Writer = writer;
    }

    /// <summary>Short name used in diagnostics and thread names: <c>console</c>, <c>file</c>, <c>json</c>.</summary>
    public string Name { get; }

    public ILineWriter Writer { get; }

    /// <summary>False drops every event before formatting.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Exceptions thrown while formatting or handing off, swallowed so logging never fails the caller.</summary>
    public long Faults => Volatile.Read(ref _faults);

    /// <summary>Lines the writer dropped because its queue was full (0 for a synchronous writer).</summary>
    public long Dropped => Writer is QueuedLineWriter queued ? queued.Dropped : 0;

    public abstract void Emit<TState>(in LogEvent evt, TState state, IExternalScopeProvider? scopes);

    /// <summary>The line this sink writes to report <paramref name="dropped"/> lost lines (its own format, at Warning).</summary>
    public abstract string DropNotice(long dropped);

    public void CountFault() => Interlocked.Increment(ref _faults);

    public void Flush() => Writer.Flush();

    public void Dispose() => Writer.Dispose();
}

/// <summary>The plain or coloured text line (<see cref="LogLineFormatter"/>); console and file sinks.</summary>
public sealed class TextLogSink(string name, ILineWriter writer, bool color) : LogSink(name, writer)
{
    private volatile bool _color = color;

    /// <summary>ANSI colour on or off; live.</summary>
    public bool Color
    {
        get => _color;
        set => _color = value;
    }

    public override void Emit<TState>(in LogEvent evt, TState state, IExternalScopeProvider? scopes)
    {
        var buffer = new LogBuffer(stackalloc char[512]);
        try
        {
            LogLineFormatter.Write(ref buffer, in evt, scopes, _color);
            Writer.Write(buffer.Written);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    public override string DropNotice(long dropped)
    {
        var buffer = new LogBuffer(stackalloc char[256]);
        try
        {
            var evt = new LogEvent(DateTime.UtcNow, LogLevel.Warning, "ArcaneCore.Logging", default, $"{Name} sink dropped {dropped} log line(s): the queue was full.", null);
            LogLineFormatter.Write(ref buffer, in evt, null, _color);
            return buffer.Written.ToString();
        }
        finally
        {
            buffer.Dispose();
        }
    }
}

/// <summary>The JSON-lines format (<see cref="JsonLineFormatter"/>).</summary>
public sealed class JsonLogSink(string name, ILineWriter writer) : LogSink(name, writer)
{
    public override void Emit<TState>(in LogEvent evt, TState state, IExternalScopeProvider? scopes)
    {
        var buffer = new LogBuffer(stackalloc char[768]);
        try
        {
            JsonLineFormatter.Write(ref buffer, in evt, state, scopes);
            Writer.Write(buffer.Written);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    public override string DropNotice(long dropped)
    {
        var buffer = new LogBuffer(stackalloc char[256]);
        try
        {
            var evt = new LogEvent(DateTime.UtcNow, LogLevel.Warning, "ArcaneCore.Logging", default, $"{Name} sink dropped {dropped} log line(s): the queue was full.", null);
            JsonLineFormatter.Write<object?>(ref buffer, in evt, null, null);
            return buffer.Written.ToString();
        }
        finally
        {
            buffer.Dispose();
        }
    }
}

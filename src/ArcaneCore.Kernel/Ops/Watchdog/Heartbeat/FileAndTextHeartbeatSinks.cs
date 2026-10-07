using System.Globalization;
using System.Text;

namespace ArcaneCore.Kernel.Ops.Watchdog.Heartbeat;

/// <summary>
/// A liveness file: every beat rewrites it (write to a sibling temporary file, then an atomic
/// rename over the target) with <c>&lt;UTC ISO-8601&gt; pid=&lt;pid&gt; &lt;state&gt;</c>, so a
/// supervisor that checks the modification time or the content never reads a half-written file.
/// The last line written at stop says <c>stopping</c>; the file is deliberately left in place.
/// </summary>
public sealed class FileHeartbeatSink : IHeartbeatSink
{
    private readonly string _path;
    private readonly string _temporary;
    private readonly int _processId;
    private readonly Func<DateTimeOffset> _now;

    public FileHeartbeatSink(string path, int processId, Func<DateTimeOffset>? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _temporary = _path + ".tmp";
        _processId = processId;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Description = "file " + _path;
    }

    public string Description { get; }

    public string? LastError { get; private set; }

    public bool Ready(string status) => Write("ready");

    public bool Beat() => Write("alive");

    public bool Stopping(string status) => Write("stopping");

    public void Dispose()
    {
    }

    private bool Write(string state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_temporary, FormatLine(_now(), _processId, state), new UTF8Encoding(false));
            File.Move(_temporary, _path, overwrite: true);
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>The file content for one beat.</summary>
    public static string FormatLine(DateTimeOffset now, int processId, string state)
        => now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + " pid=" + processId.ToString(CultureInfo.InvariantCulture) + " " + state + "\n";
}

/// <summary>One <c>heartbeat &lt;UTC&gt; pid=&lt;pid&gt; &lt;state&gt;</c> line per beat on a text writer (standard output for a supervisor that reads the console).</summary>
public sealed class TextWriterHeartbeatSink : IHeartbeatSink
{
    private readonly TextWriter _writer;
    private readonly int _processId;
    private readonly Func<DateTimeOffset> _now;

    public TextWriterHeartbeatSink(TextWriter writer, int processId, string description = "stdout", Func<DateTimeOffset>? now = null)
    {
        _writer = writer;
        _processId = processId;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Description = description;
    }

    public string Description { get; }

    public string? LastError { get; private set; }

    public bool Ready(string status) => Write("ready");

    public bool Beat() => Write("alive");

    public bool Stopping(string status) => Write("stopping");

    public void Dispose()
    {
    }

    private bool Write(string state)
    {
        try
        {
            _writer.Write("heartbeat " + FileHeartbeatSink.FormatLine(_now(), _processId, state));
            _writer.Flush();
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            LastError = ex.Message;
            return false;
        }
    }
}

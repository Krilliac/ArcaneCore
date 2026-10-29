using System.Text;

namespace ArcaneCore.World.Cluster.Supervision;

/// <summary>
/// Splits a child's output into lines at <c>\n</c> only (one <c>\r</c> directly before it is dropped, for CRLF output). <see cref="StreamReader.ReadLineAsync()"/>
/// also ends a line at a lone <c>\r</c>; a child's output carries logged text that players and staff influence, and the supervisor's protocol lines
/// are matched whole, so a CR inside such text must not be able to begin a second line that looks like one.
/// </summary>
public static class SupervisorLineReader
{
    /// <summary>The longest line kept whole; a longer one is delivered in pieces of this size.</summary>
    public const int MaxLineLength = 64 * 1024;

    /// <summary>Reads <paramref name="reader"/> to its end and calls <paramref name="onLine"/> for each line. A final line without a newline is delivered at the end. Stream errors propagate.</summary>
    public static async Task ReadLinesAsync(TextReader reader, Action<string> onLine)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(onLine);

        var buffer = new char[4096];
        var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                if (c == '\n')
                {
                    Deliver(line, onLine, dropCarriageReturn: true);
                }
                else
                {
                    line.Append(c);
                    if (line.Length >= MaxLineLength)
                    {
                        Deliver(line, onLine, dropCarriageReturn: false);
                    }
                }
            }
        }

        if (line.Length > 0)
        {
            Deliver(line, onLine, dropCarriageReturn: true);
        }
    }

    private static void Deliver(StringBuilder line, Action<string> onLine, bool dropCarriageReturn)
    {
        int length = line.Length;
        if (dropCarriageReturn && length > 0 && line[length - 1] == '\r')
        {
            length--;
        }

        string text = line.ToString(0, length);
        line.Clear();
        onLine(text);
    }
}

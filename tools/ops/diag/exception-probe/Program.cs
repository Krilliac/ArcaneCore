// Names the exceptions a running daemon throws, without pausing it: listens to the runtime's Exception events over EventPipe for a few
// seconds and prints how often each exception type + message was thrown and which method caught it. Read-only; the target keeps running.
// The Win32 debugger (attach-debugger.ps1) only sees exception code 0xE0434352, and the crash handler leaves cancellations, socket
// errors and other expected types out of the log, so this is how a first-chance storm is traced to its code.
//   dotnet run --project tools/ops/diag/exception-probe -- [pid|World|Realm] [seconds=60]
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

string target = args.Length > 0 ? args[0] : "World";
int seconds = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 60;
int pid;
if (!int.TryParse(target, out pid))
{
    Process[] found = Process.GetProcessesByName("ArcaneCore." + target);
    if (found.Length != 1)
    {
        Console.Error.WriteLine($"{found.Length} ArcaneCore.{target} processes are running; pass the PID.");
        return 2;
    }

    pid = found[0].Id;
}

var providers = new[]
{
    new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, (long)ClrTraceEventParser.Keywords.Exception),
};
var client = new DiagnosticsClient(pid);
using EventPipeSession session = client.StartEventPipeSession(providers, requestRundown: false);
var thrown = new Dictionary<string, int>();
var caught = new Dictionary<string, int>();
var lastTypeByThread = new Dictionary<int, string>();
var gate = new object();
using var source = new EventPipeEventSource(session.EventStream);
source.Clr.ExceptionStart += e =>
{
    string key = e.ExceptionType + " | " + Fold(e.ExceptionMessage);
    lock (gate)
    {
        thrown[key] = thrown.GetValueOrDefault(key) + 1;
        lastTypeByThread[e.ThreadID] = e.ExceptionType;
    }
};
source.Clr.ExceptionCatchStart += e =>
{
    lock (gate)
    {
        string key = (lastTypeByThread.GetValueOrDefault(e.ThreadID) ?? "?") + " caught in " + e.MethodName;
        caught[key] = caught.GetValueOrDefault(key) + 1;
    }
};
Task processing = Task.Run(source.Process);
Console.WriteLine($"listening to PID {pid} for {seconds} s ...");
await Task.Delay(TimeSpan.FromSeconds(seconds));
session.Stop();
await Task.WhenAny(processing, Task.Delay(TimeSpan.FromSeconds(10)));

lock (gate)
{
    Console.WriteLine($"== thrown in {seconds} s ({thrown.Values.Sum()} total):");
    foreach (KeyValuePair<string, int> kv in thrown.OrderByDescending(k => k.Value).Take(25))
    {
        Console.WriteLine($"{kv.Value,7} {kv.Key}");
    }

    Console.WriteLine("== caught in:");
    foreach (KeyValuePair<string, int> kv in caught.OrderByDescending(k => k.Value).Take(25))
    {
        Console.WriteLine($"{kv.Value,7} {kv.Key}");
    }
}

return 0;

// Numbers (ids, positions, timings) folded so one throw site groups into one line.
static string Fold(string? message)
{
    string folded = Regex.Replace(message ?? string.Empty, @"\d+", "N");
    return folded.Length > 160 ? folded[..160] : folded;
}

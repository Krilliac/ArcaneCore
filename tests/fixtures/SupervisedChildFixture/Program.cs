// A throwaway child for the real-launcher tests; never an ArcaneCore daemon. It ignores stdin, so "drain" and EOF do nothing.
// Arguments:
//   --exit <code>              exit at once with <code> (the "well-behaved child" case)
//   --spawn-child              start a copy of itself first, for the tree-kill case (the copy gets the same --lifetime-ms)
//   --pid-file <path>          append "self <pid>" and, after --spawn-child, "child <pid>" (race-free alternative to parsing stdout)
//   --lifetime-ms <n>          maximum life in milliseconds (default 60000)
//   --cr-heartbeat             after "ready", write one line "noise<CR>heartbeat <utc> pid=<pid> alive" (a bare CR, not CRLF), then the line "done"
// Otherwise: print "ready" on stdout and sleep for the bounded lifetime, then exit 0. The bound is what cleans up after a failing test:
// nothing ever kills a fixture by pid, so a fixture that is left behind ends by itself.
using System.Diagnostics;
using System.Globalization;

int exitAt = Array.IndexOf(args, "--exit");
if (exitAt >= 0)
    return int.Parse(args[exitAt + 1], CultureInfo.InvariantCulture);

int lifetimeAt = Array.IndexOf(args, "--lifetime-ms");
int lifetimeMs = lifetimeAt >= 0 ? int.Parse(args[lifetimeAt + 1], CultureInfo.InvariantCulture) : 60_000;

string? pidFile = Array.IndexOf(args, "--pid-file") is >= 0 and var at ? args[at + 1] : null;
if (pidFile is not null)
    File.AppendAllText(pidFile, $"self {Environment.ProcessId}\n");

if (args.Contains("--spawn-child"))
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    info.ArgumentList.Add(typeof(Program).Assembly.Location);
    info.ArgumentList.Add("--lifetime-ms");
    info.ArgumentList.Add(lifetimeMs.ToString(CultureInfo.InvariantCulture));
    using Process child = Process.Start(info)!;
    if (pidFile is not null)
        File.AppendAllText(pidFile, $"child {child.Id}\n");
}

Console.Out.WriteLine("ready");
Console.Out.Flush();
if (args.Contains("--cr-heartbeat"))
{
    // A bare CR inside one LF-terminated line: a reader that also splits at CR would see a second line that is a valid heartbeat.
    Console.Out.Write($"noise\rheartbeat 2026-10-29T00:00:00.000Z pid={Environment.ProcessId} alive\n");
    Console.Out.Write("done\n");
    Console.Out.Flush();
}

Thread.Sleep(lifetimeMs);
return 0;

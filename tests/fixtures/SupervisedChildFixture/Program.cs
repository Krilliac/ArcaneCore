// Arguments:
//   --exit <code>              exit at once with <code> (the "well-behaved child" case)
//   --spawn-child              start a copy of itself (no arguments) first, for the tree-kill case
//   --pid-file <path>          append "self <pid>" and, after --spawn-child, "child <pid>" (race-free alternative to parsing stdout)
// Otherwise: print "ready" on stdout and sleep forever, ignoring stdin.
using System.Diagnostics;
using System.Globalization;

int exitAt = Array.IndexOf(args, "--exit");
if (exitAt >= 0)
    return int.Parse(args[exitAt + 1], CultureInfo.InvariantCulture);

string? pidFile = Array.IndexOf(args, "--pid-file") is >= 0 and var at ? args[at + 1] : null;
if (pidFile is not null)
    File.AppendAllText(pidFile, $"self {Environment.ProcessId}\n");

if (args.Contains("--spawn-child"))
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    info.ArgumentList.Add(typeof(Program).Assembly.Location);
    using Process child = Process.Start(info)!;
    if (pidFile is not null)
        File.AppendAllText(pidFile, $"child {child.Id}\n");
}

Console.Out.WriteLine("ready");
Console.Out.Flush();
Thread.Sleep(Timeout.Infinite);
return 0;

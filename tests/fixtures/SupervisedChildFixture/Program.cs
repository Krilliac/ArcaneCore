// A throwaway child for the real-launcher tests; never an ArcaneCore daemon. It ignores stdin, so "drain" and EOF do nothing.
// Arguments:
//   --exit <code>              exit at once with <code> (the "well-behaved child" case)
//   --spawn-child              start a copy of itself first, for the tree-kill case (the copy gets the same --lifetime-ms)
//   --pid-file <path>          append "self <pid>" and, after --spawn-child, "child <pid>" (race-free alternative to parsing stdout)
//   --lifetime-ms <n>          maximum life in milliseconds (default 60000)
//   --stack-depth <bytes>      run a 2 KB-a-frame recursion, guarded by the production StackGuard.TryEnter, on a thread created with that stack size, print "depth <n>" and exit 0 (stack-guard tests)
//   --game-dll <path>          the ArcaneCore.Game.dll that holds StackGuard (internal: reached by reflection, so the fixture needs no project reference); required with --stack-depth
//   --unguarded                with --stack-depth: suspend the production guard on the recursion thread (StackGuard.Suspend), so the recursion overflows the stack and the process dies (the RED run)
//   --cr-heartbeat             after "ready", write one line "noise<CR>heartbeat <utc> pid=<pid> alive" (a bare CR, not CRLF), then the line "done"
// Otherwise: print "ready" on stdout and sleep for the bounded lifetime, then exit 0. The bound is what cleans up after a failing test:
// nothing ever kills a fixture by pid, so a fixture that is left behind ends by itself.
using System.Diagnostics;
using System.Globalization;

int exitAt = Array.IndexOf(args, "--exit");
if (exitAt >= 0)
    return int.Parse(args[exitAt + 1], CultureInfo.InvariantCulture);

int stackAt = Array.IndexOf(args, "--stack-depth");
if (stackAt >= 0)
{
    int stackBytes = int.Parse(args[stackAt + 1], CultureInfo.InvariantCulture);
    int gameAt = Array.IndexOf(args, "--game-dll");
    if (gameAt < 0)
    {
        Console.Error.WriteLine("--stack-depth needs --game-dll <path>");
        return 2;
    }

    StackProbe.Bind(args[gameAt + 1]);
    bool unguarded = args.Contains("--unguarded");
    int depth = -1;
    var probe = new Thread(() => depth = StackProbe.Run(unguarded), stackBytes);
    probe.Start();
    probe.Join();
    Console.Out.WriteLine($"depth {depth}");
    Console.Out.Flush();
    return 0;
}

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
    // It carries the supervisor's real per-run token when it has one, so a CR-split line is rejected for the CR and not merely for a missing token.
    string? runToken = Environment.GetEnvironmentVariable("ARCANECORE_SUPERVISOR_TOKEN");
    string tokenSuffix = string.IsNullOrEmpty(runToken) ? string.Empty : " token=" + runToken;
    Console.Out.Write($"noise\rheartbeat 2026-10-29T00:00:00.000Z pid={Environment.ProcessId} alive{tokenSuffix}\n");
    Console.Out.Write("done\n");
    Console.Out.Flush();
}

Thread.Sleep(lifetimeMs);
return 0;

/// <summary>
/// Recursion that stops where the production <c>ArcaneCore.Game.StackGuard.TryEnter</c> says the stack is nearly gone. StackGuard is internal, so it is bound by
/// reflection from the Game assembly file (its dependencies sit beside it). Unguarded (<c>StackGuard.Suspend</c>), the recursion has nothing to stop it and ends the process.
/// </summary>
internal static class StackProbe
{
    private static System.Reflection.MethodInfo? s_tryEnter;
    private static System.Reflection.MethodInfo? s_suspend;

    public static void Bind(string gameDll)
    {
        var game = System.Reflection.Assembly.LoadFrom(gameDll);
        Type guard = game.GetType("ArcaneCore.Game.StackGuard", throwOnError: true)!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        s_tryEnter = guard.GetMethod("TryEnter", flags) ?? throw new MissingMethodException("StackGuard.TryEnter");
        s_suspend = guard.GetMethod("Suspend", flags) ?? throw new MissingMethodException("StackGuard.Suspend");
    }

    public static int Run(bool unguarded)
    {
        // Warm the exception machinery on a roomy stack, as the guard's own fault record needs some at the margin.
        try
        {
            throw new InsufficientExecutionStackException("warm-up");
        }
        catch (InsufficientExecutionStackException)
        {
        }

        IDisposable? scope = unguarded ? (IDisposable)s_suspend!.Invoke(null, null)! : null;
        try
        {
            return Recurse(0);
        }
        finally
        {
            scope?.Dispose();
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Recurse(int depth)
    {
        if (!(bool)s_tryEnter!.Invoke(null, ["fixture.stack-probe", null])!)
        {
            return depth;
        }

        Span<byte> pad = stackalloc byte[2048];
        pad[0] = 1;
        int result = Recurse(depth + 1);
        return result + pad[0] - 1; // keeps the frame alive: not a tail call
    }
}

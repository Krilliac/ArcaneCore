// Names what a running daemon allocates on the large object heap (and, separately, its heaviest small-object allocators), without pausing
// it: listens to the runtime's GCAllocationTick events over EventPipe for a few seconds and prints, per allocated type and per allocating
// call stack, how many sampled megabytes went to the LOH. A tick fires about once per 100 KB allocated, so every LOH allocation (85 KB and
// up) is close to one tick: the LOH counts are near exact, the small-object ones are a sample. Read-only; the target keeps running (a
// method rundown at the end names the JIT-compiled frames).
//   dotnet run --project tools/ops/diag/alloc-probe -- [pid|World|Realm] [seconds=30] [frames=8]
using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

string target = args.Length > 0 ? args[0] : "World";
int seconds = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 30;
int depth = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 8;
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
    // GC keyword at Verbose is what carries GCAllocationTick; the stacks come with every EventPipe event.
    new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, (long)ClrTraceEventParser.Keywords.GC),
};
string file = Path.Combine(Path.GetTempPath(), $"alloc-probe-{pid}-{DateTime.UtcNow:yyyyMMddHHmmss}.nettrace");
var client = new DiagnosticsClient(pid);
Console.WriteLine($"listening to PID {pid} for {seconds} s ...");
using (EventPipeSession session = client.StartEventPipeSession(providers, requestRundown: true))
{
    await using (FileStream output = File.Create(file))
    {
        Task copy = session.EventStream.CopyToAsync(output);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        session.Stop();
        await Task.WhenAny(copy, Task.Delay(TimeSpan.FromSeconds(60)));
    }
}

var largeByType = new Dictionary<string, (long Bytes, int Ticks)>();
var largeByStack = new Dictionary<string, (long Bytes, int Ticks)>();
var smallByType = new Dictionary<string, (long Bytes, int Ticks)>();
long largeTotal = 0;
long smallTotal = 0;
string etlx = TraceLog.CreateFromEventPipeDataFile(file);
using (var log = new TraceLog(etlx))
{
    foreach (GCAllocationTickTraceData tick in log.Events.ByEventType<GCAllocationTickTraceData>())
    {
        if (tick.ProcessID != pid && tick.ProcessID != 0 && tick.ProcessID != -1)
        {
            continue;
        }

        long amount = tick.AllocationAmount64 > 0 ? tick.AllocationAmount64 : tick.AllocationAmount;
        string type = string.IsNullOrEmpty(tick.TypeName) ? "?" : tick.TypeName;
        if (tick.AllocationKind == GCAllocationKind.Large)
        {
            largeTotal += amount;
            Add(largeByType, type, amount);
            Add(largeByStack, type + Environment.NewLine + Stack(tick.CallStack(), depth), amount);
        }
        else
        {
            smallTotal += amount;
            Add(smallByType, type, amount);
        }
    }
}

Console.WriteLine($"== LOH: {largeTotal / 1048576.0:F1} MB sampled in {seconds} s ({largeTotal / 1048576.0 / seconds:F2} MB/s), by type:");
foreach ((string key, (long bytes, int ticks)) in largeByType.OrderByDescending(k => k.Value.Bytes).Take(20))
{
    Console.WriteLine($"{bytes / 1048576.0,9:F1} MB {ticks,6} ticks  {key}");
}

Console.WriteLine("== LOH by allocating stack:");
foreach ((string key, (long bytes, int ticks)) in largeByStack.OrderByDescending(k => k.Value.Bytes).Take(12))
{
    Console.WriteLine($"{bytes / 1048576.0,9:F1} MB {ticks,6} ticks  {key}");
    Console.WriteLine();
}

Console.WriteLine($"== small object heap: {smallTotal / 1048576.0:F1} MB sampled ({smallTotal / 1048576.0 / seconds:F1} MB/s), by type:");
foreach ((string key, (long bytes, int ticks)) in smallByType.OrderByDescending(k => k.Value.Bytes).Take(15))
{
    Console.WriteLine($"{bytes / 1048576.0,9:F1} MB {ticks,6} ticks  {key}");
}

try
{
    File.Delete(file);
    File.Delete(etlx);
}
catch (IOException)
{
    // A trace still open elsewhere stays in the temp folder.
}

return 0;

static void Add(Dictionary<string, (long Bytes, int Ticks)> map, string key, long amount)
{
    (long bytes, int ticks) = map.GetValueOrDefault(key);
    map[key] = (bytes + amount, ticks + 1);
}

// The innermost frames below the runtime's own allocation helpers, one per line.
static string Stack(TraceCallStack? stack, int depth)
{
    var frames = new List<string>();
    for (TraceCallStack? frame = stack; frame is not null && frames.Count < depth; frame = frame.Caller)
    {
        string name = frame.CodeAddress.FullMethodName;
        if (string.IsNullOrEmpty(name))
        {
            continue; // native runtime frames (the allocator itself) carry no managed name
        }

        frames.Add("    " + (name.Length > 200 ? name[..200] : name));
    }

    return frames.Count == 0 ? "    (no managed frames)" : string.Join(Environment.NewLine, frames);
}

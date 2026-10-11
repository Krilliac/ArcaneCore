using System.Text;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.Diagnostics;
using ArcaneCore.World.Ops.ServerConsole;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.OpsDiagnostics;

/// <summary><c>.debug gc|alloc|dump</c> through the real command table, as the server console (no character needed).</summary>
public sealed class DiagnosticsCommandTests
{
    private sealed class LineWriter : TextWriter
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                _lines.Add(value ?? string.Empty);
            }
        }

        public string[] Lines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _lines];
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _lines.Clear();
            }
        }
    }

    private sealed class Writer : IProcessDumpWriter
    {
        public List<DumpKind> Kinds { get; } = [];

        public void Write(DumpKind kind, string path)
        {
            lock (Kinds)
            {
                Kinds.Add(kind);
            }

            File.WriteAllBytes(path, new byte[2048]);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly WorldTestHost _host;
        private readonly AsyncServiceScope _scope;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-cmd-dump-" + Guid.NewGuid().ToString("N"));

        public Rig(bool dumpsEnabled)
        {
            DumpWriter = new Writer();
            var service = new ProcessDumpService(new DumpDiagnosticsOptions { Enabled = dumpsEnabled }, _dir, NullLogger.Instance, DumpWriter, freeSpace: _ => 1L << 50);
            Profiler = new AllocationProfiler(_ => new Listener());
            _host = WorldTestHost.Start(configureServices: s =>
            {
                s.AddSingleton(service);
                s.AddSingleton(Profiler);
            });
            Output = new LineWriter();
            _scope = _host.WorldServices.CreateAsyncScope();
            Executor = WorldConsoleExecutor.Create(_scope.ServiceProvider, _host.Opcodes, _host.World, _host.Registry, new WorldSessionOptions(), NullLogger.Instance, Output);
        }

        private sealed class Listener : IDisposable
        {
            public void Dispose()
            {
            }
        }

        public Writer DumpWriter { get; }

        public AllocationProfiler Profiler { get; }

        public LineWriter Output { get; }

        public WorldConsoleExecutor Executor { get; }

        public async Task<string[]> RunAsync(string line)
        {
            Output.Clear();
            await _host.World.InvokeAsync(() =>
            {
                Executor.Execute(line);
                return 0;
            });
            return Output.Lines;
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            Profiler.Dispose();
            await _host.DisposeAsync();
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
    }

    private static string Joined(string[] lines) => string.Join("\n", lines);

    [Fact]
    public async Task DebugGc_PrintsTheGcState()
    {
        await using var rig = new Rig(dumpsEnabled: false);
        string[] lines = await rig.RunAsync("debug gc");
        string all = Joined(lines);
        Assert.Contains("GC mode:", all);
        Assert.Contains("GC totals:", all);
        Assert.Contains("Memory: load", all);
        Assert.Contains("Ephemeral", all);
        Assert.Contains("FullBlocking", all);
        Assert.Contains("Background", all);
        Assert.DoesNotContain("The command failed", all);
    }

    [Fact]
    public async Task DebugAlloc_StartReportStop_RunsThroughTheProfiler()
    {
        await using var rig = new Rig(dumpsEnabled: false);
        Assert.Contains("No allocation sample has run", Joined(await rig.RunAsync("debug alloc report")));
        Assert.Contains("started for 45 s", Joined(await rig.RunAsync("debug alloc start seconds=45")));
        Assert.True(rig.Profiler.IsRunning);
        Assert.Contains("already running", Joined(await rig.RunAsync("debug alloc start")));
        string report = Joined(await rig.RunAsync("debug alloc report top=3"));
        Assert.Contains("Allocation sample (running)", report);
        Assert.Contains("about one allocation-tick event per 100 KB", report);
        Assert.Contains("stopped", Joined(await rig.RunAsync("debug alloc stop")));
        Assert.False(rig.Profiler.IsRunning);
        Assert.Contains("Allocation sample (stopped)", Joined(await rig.RunAsync("debug alloc report")));
        Assert.Contains("No allocation sample is running", Joined(await rig.RunAsync("debug alloc stop")));
    }

    [Fact]
    public async Task DebugAlloc_BadArguments_AreRejected()
    {
        await using var rig = new Rig(dumpsEnabled: false);
        Assert.Contains("seconds must be 1-600", Joined(await rig.RunAsync("debug alloc start 0")));
        Assert.Contains("top must be", Joined(await rig.RunAsync("debug alloc report 0")));
        Assert.Contains("Syntax: .debug alloc start", Joined(await rig.RunAsync("debug alloc start soon")));
        Assert.False(rig.Profiler.IsRunning);
    }

    [Fact]
    public async Task DebugDump_WhenDisabled_IsRefused_AndWritesNothing()
    {
        await using var rig = new Rig(dumpsEnabled: false);
        string all = Joined(await rig.RunAsync("debug dump"));
        Assert.Contains("Process dumps are disabled", all);
        Assert.Empty(rig.DumpWriter.Kinds);
    }

    [Fact]
    public async Task DebugDump_WhenEnabled_DefaultsToFull_RepliesStarted_ThenReportsTheOutcome()
    {
        await using var rig = new Rig(dumpsEnabled: true);
        string started = Joined(await rig.RunAsync("debug dump"));
        Assert.Contains("Dump started: full", started);
        Assert.Contains("roughly 1 minute at about 7 GB", started);

        await WorldTestHost.WaitForAsync(() => rig.Output.Lines.Any(l => l.Contains("Dump written")), "the completion reply");
        Assert.Equal([DumpKind.Full], rig.DumpWriter.Kinds);
    }

    [Fact]
    public async Task DebugDump_Syntax_AcceptsFullHeapMini_AndRejectsTheRest()
    {
        await using var rig = new Rig(dumpsEnabled: true);
        Assert.Contains("Syntax: .debug dump", Joined(await rig.RunAsync("debug dump everything")));
        Assert.Empty(rig.DumpWriter.Kinds);
        Assert.Contains("Dump started: mini", Joined(await rig.RunAsync("debug dump MINI")));
        await WorldTestHost.WaitForAsync(() => rig.Output.Lines.Any(l => l.Contains("Dump written")), "the completion reply");
        Assert.True(DiagnosticsArgs.TryDumpKind("heap", out DumpKind heap));
        Assert.Equal(DumpKind.Heap, heap);
        Assert.False(DiagnosticsArgs.TryOption("seconds=-1", "seconds", 30, out _));
        Assert.True(DiagnosticsArgs.TryOption("Seconds=12", "seconds", 30, out int twelve));
        Assert.Equal(12, twelve);
        Assert.True(DiagnosticsArgs.TryOption("  ", "seconds", 30, out int dflt));
        Assert.Equal(30, dflt);
    }

    [Fact]
    public void Permissions_GcIsGameMaster_AllocAndDumpAreAdministrator_AndTheyHaveHelpText()
    {
        CommandTable table = ChatCommands.CreateTable();
        var options = new ArcaneCore.World.Gm.Core.GmOptions();
        ChatCommand debug = table.Roots.Single(c => c.Name == "debug");
        ChatCommand gc = debug.SubCommands.Single(c => c.Name == "gc");
        ChatCommand alloc = debug.SubCommands.Single(c => c.Name == "alloc");
        ChatCommand dump = debug.SubCommands.Single(c => c.Name == "dump");

        Assert.Equal(AccountSecurity.GameMaster, gc.Security);
        Assert.Equal(AccountSecurity.Administrator, alloc.Security);
        Assert.Equal(AccountSecurity.Administrator, dump.Security);
        Assert.True(gc.RequiredLevel(options) < dump.RequiredLevel(options));
        Assert.Equal(["start", "report", "stop"], alloc.SubCommands.Select(c => c.Name));
        Assert.All(alloc.SubCommands, c => Assert.Equal(AccountSecurity.Administrator, c.Security));
        Assert.All(new[] { gc, alloc, dump }.Concat(alloc.SubCommands), c => Assert.StartsWith("Syntax: .debug", c.Help));
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// tools/ops/diag/server-status.ps1 -Once: the seven split-cluster node rows (old cluster-node-bridge topology) appear only for ports
/// something listens on. The scan range is moved with -SplitNodePortBase to seven consecutive free loopback ports, so the result does not
/// depend on what runs on the host. Nothing is started but plain TCP listeners owned by the test.
/// </summary>
public sealed class ServerStatusSplitNodesTests : IDisposable
{
    private static readonly string Script = Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "diag", "server-status.ps1");
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "arcane-status-" + Guid.NewGuid().ToString("N"));

    public ServerStatusSplitNodesTests()
    {
        Directory.CreateDirectory(_profile);
        File.WriteAllText(Path.Combine(_profile, "appsettings.json"), "{}");
    }

    public void Dispose() => Directory.Delete(_profile, recursive: true);

    [PwshFact]
    public void NoSplitNodeListening_PrintsOneSummaryLineAndNoNotListeningRows()
    {
        (int baseport, List<TcpListener> held) = ReserveRange();
        foreach (TcpListener l in held) { l.Stop(); }

        string output = Run(baseport);

        Assert.Contains($"split nodes      none running (ports {baseport}-{baseport + 6}; the combined world hosts every map)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("worker:EK", output, StringComparison.Ordinal);
        Assert.DoesNotContain("coordinator", output, StringComparison.Ordinal);
        Assert.Contains("Realm", output, StringComparison.Ordinal);
        Assert.Contains("World", output, StringComparison.Ordinal);
        Assert.Contains("NOT LISTENING on port", output, StringComparison.Ordinal); // Realm and World rows stay
    }

    [PwshFact]
    public void SomeSplitNodesListening_PrintsThoseRowsAndNamesTheRest()
    {
        (int baseport, List<TcpListener> held) = ReserveRange();
        // Keep ports +1 (worker:Kalimdor) and +3 (gateway) listening; release the other five.
        var keep = new HashSet<int> { baseport + 1, baseport + 3 };
        try
        {
            foreach (TcpListener l in held)
            {
                if (!keep.Contains(((IPEndPoint)l.LocalEndpoint).Port)) { l.Stop(); }
            }

            string output = Run(baseport);

            Assert.Contains("worker:Kalimdor", output, StringComparison.Ordinal);
            Assert.Contains("gateway", output, StringComparison.Ordinal);
            Assert.DoesNotContain("worker:EK", output, StringComparison.Ordinal);
            Assert.DoesNotContain("none running", output, StringComparison.Ordinal);
            Assert.Contains($"split nodes      not listening: ports {baseport}, {baseport + 2}, {baseport + 4}, {baseport + 5}, {baseport + 6}", output, StringComparison.Ordinal);
        }
        finally
        {
            foreach (TcpListener l in held) { l.Stop(); }
        }
    }

    private static (int Base, List<TcpListener> Held) ReserveRange()
    {
        var rng = new Random();
        for (int attempt = 0; attempt < 200; attempt++)
        {
            int start = rng.Next(20000, 32000);
            var held = new List<TcpListener>();
            try
            {
                for (int i = 0; i < 7; i++)
                {
                    var l = new TcpListener(IPAddress.Loopback, start + i);
                    l.Start();
                    held.Add(l);
                }

                return (start, held);
            }
            catch (SocketException)
            {
                foreach (TcpListener l in held) { l.Stop(); }
            }
        }

        throw new InvalidOperationException("no seven consecutive free ports found");
    }

    private string Run(int splitBase)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in new[]
        {
            "-NoProfile", "-NonInteractive", "-File", Script, "-ProfileDir", _profile, "-Once",
            "-WorldPort", "1", "-AuthPort", "2", "-SplitNodePortBase", splitBase.ToString(System.Globalization.CultureInfo.InvariantCulture),
        })
        {
            start.ArgumentList.Add(a);
        }

        using Process process = Process.Start(start)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("server-status.ps1 -Once did not finish within 120 s");
        }

        string all = stdout.Result + stderr.Result;
        Assert.True(process.ExitCode == 0, all);
        return all;
    }

    private sealed class PwshFactAttribute : FactAttribute
    {
        public PwshFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Windows status console; the listener probe (Get-NetTCPConnection / Windows netstat) does not exist elsewhere.";
            }
            else if (!Available.Value)
            {
                Skip = "pwsh (PowerShell 7) is not on PATH on this host.";
            }
        }
    }

    private static class Available
    {
        public static readonly bool Value = Probe();

        private static bool Probe()
        {
            try
            {
                var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.Major"])
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using Process process = Process.Start(start)!;
                string text = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return process.ExitCode == 0 && int.TryParse(text, out int major) && major >= 7;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }
}

using System.Diagnostics;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>The report's process uptime is measured from the process start (live 2026-10-26: it was measured from the first report, so it ran short and could go negative).</summary>
[Collection(DiagnosticsCollection.Name)]
public sealed class CrashReportUptimeTests
{
    [Fact]
    public void Render_Uptime_IsMeasuredFromTheProcessStart()
    {
        DateTime started;
        using (Process process = Process.GetCurrentProcess())
        {
            started = process.StartTime.ToUniversalTime();
        }

        string report = CrashReport.Render(CrashKind.FirstChance, null, [], started.AddSeconds(90));

        Assert.Contains("  uptime: 00:01:30\n", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Uptime_IsNeverNegative()
    {
        string report = CrashReport.Render(CrashKind.FirstChance, null, [], DateTime.UnixEpoch);

        Assert.Contains("  uptime: 00:00:00\n", report, StringComparison.Ordinal);
    }
}

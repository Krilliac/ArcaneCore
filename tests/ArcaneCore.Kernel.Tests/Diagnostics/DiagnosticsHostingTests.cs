using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>The one-line Program.cs wiring: binding, the bootstrap hooks, and the swap to the host's logger at start.</summary>
[Collection(DiagnosticsCollection.Name)]
public sealed class DiagnosticsHostingTests : IDisposable
{
    public void Dispose()
    {
        CrashHandler.Current?.Dispose();
        Invariant.Configure(new DiagnosticsOptions(), logger: null);
    }

    [Fact]
    public async Task UseArcaneDiagnostics_BindsTheSection_InstallsTheHooksAtOnce_AndSwitchesToTheLoggerOnStart()
    {
        var log = new CapturingLogger();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            Args = ["--Diagnostics:OnUnhandled=Exit", "--Diagnostics:OnInvariant=Continue", "--Diagnostics:InvariantLogLimit=3", "--Diagnostics:OnUnobservedTask=Log"],
        });
        builder.Logging.ClearProviders();
        builder.Logging.Services.AddSingleton<ILoggerProvider>(new CapturingProvider(log));
        builder.Services.AddSingleton<ICrashContextProvider>(new NamedProvider("host-test"));

        builder.UseArcaneDiagnostics();

        // Bootstrap: hooks are live before Build, writing to standard error.
        CrashHandler? bootstrap = CrashHandler.Current;
        Assert.NotNull(bootstrap);
        Assert.Equal(UnhandledExceptionPolicy.Exit, bootstrap.Options.OnUnhandled);
        Assert.Same(StandardErrorCrashSink.Instance, bootstrap.Sink);

        using IHost host = builder.Build();
        DiagnosticsOptions bound = host.Services.GetRequiredService<IOptions<DiagnosticsOptions>>().Value;
        Assert.Equal(UnhandledExceptionPolicy.Exit, bound.OnUnhandled);
        Assert.Equal(3, bound.InvariantLogLimit);
        Assert.Contains(host.Services.GetServices<IHostedService>(), s => s.GetType().Name == "DiagnosticsHostedService");

        await host.StartAsync();
        try
        {
            CrashHandler? hosted = CrashHandler.Current;
            Assert.NotNull(hosted);
            Assert.Same(bootstrap, hosted);
            Assert.IsType<LoggerCrashSink>(hosted.Sink);
            Assert.Contains("[host-test]", hosted.Render(CrashKind.Unhandled, null), StringComparison.Ordinal);
            Assert.Contains(log.Entries, e => e.Contains("Diagnostics: OnInvariant=Continue", StringComparison.Ordinal) && e.Contains("OnUnhandled=Exit", StringComparison.Ordinal));

            // The invariant log now goes through the host's logger.
            Invariant.Check(false, "hosted invariant probe");
            Assert.Contains(log.Entries, e => e.StartsWith("Error: Invariant Check failed", StringComparison.Ordinal) && e.Contains("hosted invariant probe", StringComparison.Ordinal));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public void UseArcaneDiagnostics_WithoutASection_KeepsEveryDefault()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.UseArcaneDiagnostics();

        DiagnosticsOptions options = CrashHandler.Current!.Options;
        var defaults = new DiagnosticsOptions();
        Assert.Equal(defaults.OnInvariant, options.OnInvariant);
        Assert.Equal(defaults.OnUnhandled, options.OnUnhandled);
        Assert.Equal(defaults.OnUnobservedTask, options.OnUnobservedTask);
        Assert.Equal(defaults.InvariantLogLimit, options.InvariantLogLimit);
        Assert.Equal(defaults.BreakOnInvariant, options.BreakOnInvariant);
        Assert.Equal(defaults.FirstChanceExceptions, options.FirstChanceExceptions);
        Assert.Equal(InvariantPolicy.Continue, Invariant.Policy);
    }

    private sealed class NamedProvider(string name) : ICrashContextProvider
    {
        public string Name => name;

        public void Describe(System.Text.StringBuilder report) => report.Append("  present\n");
    }

    private sealed class CapturingProvider(CapturingLogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }
}

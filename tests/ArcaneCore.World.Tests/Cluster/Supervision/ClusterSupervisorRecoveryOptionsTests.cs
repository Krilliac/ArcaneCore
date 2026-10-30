using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.Cluster.Supervision;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>The crash/hang recovery keys of <c>Cluster:Supervisor</c> and of each <c>Processes:&lt;key&gt;</c>: defaults, binding and validation.</summary>
public sealed class ClusterSupervisorRecoveryOptionsTests
{
    private static ClusterSupervisorOptions Bind(params (string Key, string Value)[] values)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
        var options = new ClusterSupervisorOptions();
        configuration.GetSection(ClusterSupervisorOptions.SectionName).Bind(options);
        return options;
    }

    private static ClusterSupervisorOptions WithProcess(Action<SupervisedProcessConfig> configure, Action<ClusterSupervisorOptions>? options = null)
    {
        var o = new ClusterSupervisorOptions();
        var config = new SupervisedProcessConfig { Name = "world", Role = SupervisedRoles.Combined, ExecutablePath = "dotnet" };
        configure(config);
        o.Processes["world"] = config;
        options?.Invoke(o);
        return o;
    }

    [Fact]
    public void Defaults_AreTheApprovedPolicy_AndValid()
    {
        var supervisor = new ClusterSupervisorOptions();
        var process = new SupervisedProcessConfig();

        Assert.Equal(5, supervisor.CrashLoopMaxRestarts);
        Assert.Equal(300, supervisor.CrashLoopWindowSeconds);
        Assert.Equal(120, supervisor.HealthyUptimeSeconds);
        Assert.Equal(0, supervisor.RestartDelayMs);
        Assert.True(supervisor.StopWhenBelowMinimum);
        Assert.Equal("logs/supervisor-captures", supervisor.HangCaptureDirectory);
        Assert.Equal(300, supervisor.HangCaptureTimeoutSeconds);
        Assert.Equal(string.Empty, supervisor.StackCaptureTool);
        Assert.Equal(120, process.HangTimeoutSeconds);
        Assert.Equal(300, process.HeartbeatGraceSeconds);
        Assert.Equal(15_000, process.HangDrainTimeoutMs);
        Assert.Equal(0, process.StopGraceMs);
        Assert.Equal(SupervisedHangCaptureMode.None, process.HangCapture);
        Assert.Equal(0, process.RealmId);
        Assert.Empty(supervisor.Validate());
    }

    [Fact]
    public void Binds_TheNewKeys()
    {
        ClusterSupervisorOptions options = Bind(
            ("Cluster:Supervisor:CrashLoopMaxRestarts", "7"),
            ("Cluster:Supervisor:CrashLoopWindowSeconds", "600"),
            ("Cluster:Supervisor:HealthyUptimeSeconds", "90"),
            ("Cluster:Supervisor:RestartDelayMs", "250"),
            ("Cluster:Supervisor:StopWhenBelowMinimum", "false"),
            ("Cluster:Supervisor:HangCaptureDirectory", "caps"),
            ("Cluster:Supervisor:HangCaptureTimeoutSeconds", "60"),
            ("Cluster:Supervisor:StackCaptureTool", "dotnet-stack"),
            ("Cluster:Supervisor:Processes:world:Role", "combined"),
            ("Cluster:Supervisor:Processes:world:ExecutablePath", "dotnet"),
            ("Cluster:Supervisor:Processes:world:HangTimeoutSeconds", "45"),
            ("Cluster:Supervisor:Processes:world:HeartbeatGraceSeconds", "600"),
            ("Cluster:Supervisor:Processes:world:HangDrainTimeoutMs", "5000"),
            ("Cluster:Supervisor:Processes:world:StopGraceMs", "20000"),
            ("Cluster:Supervisor:Processes:world:HangCapture", "Stacks"),
            ("Cluster:Supervisor:Processes:world:RealmId", "4"));

        Assert.Equal(7, options.CrashLoopMaxRestarts);
        Assert.Equal(600, options.CrashLoopWindowSeconds);
        Assert.Equal(90, options.HealthyUptimeSeconds);
        Assert.Equal(250, options.RestartDelayMs);
        Assert.False(options.StopWhenBelowMinimum);
        Assert.Equal("caps", options.HangCaptureDirectory);
        Assert.Equal(60, options.HangCaptureTimeoutSeconds);
        Assert.Equal("dotnet-stack", options.StackCaptureTool);
        SupervisedProcessConfig world = options.Processes["world"];
        Assert.Equal(45, world.HangTimeoutSeconds);
        Assert.Equal(600, world.HeartbeatGraceSeconds);
        Assert.Equal(5000, world.HangDrainTimeoutMs);
        Assert.Equal(20_000, world.StopGraceMs);
        Assert.Equal(SupervisedHangCaptureMode.Stacks, world.HangCapture);
        Assert.Equal(4, world.RealmId);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(3600, true)]
    [InlineData(1, false)]
    [InlineData(29, false)]
    [InlineData(3601, false)]
    [InlineData(-1, false)]
    public void Validate_HangTimeoutSeconds_IsOffOr30To3600(int value, bool valid)
        => AssertValidity(WithProcess(c => c.HangTimeoutSeconds = value), valid, "HangTimeoutSeconds");

    [Theory]
    [InlineData(30, true)]
    [InlineData(86_400, true)]
    [InlineData(29, false)]
    [InlineData(86_401, false)]
    public void Validate_HeartbeatGraceSeconds_Is30To86400(int value, bool valid)
        => AssertValidity(WithProcess(c => c.HeartbeatGraceSeconds = value), valid, "HeartbeatGraceSeconds");

    [Theory]
    [InlineData(1, true)]
    [InlineData(600_000, true)]
    [InlineData(0, false)]
    [InlineData(600_001, false)]
    public void Validate_HangDrainTimeoutMs_IsPositiveAndCapped(int value, bool valid)
        => AssertValidity(WithProcess(c => c.HangDrainTimeoutMs = value), valid, "HangDrainTimeoutMs");

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(600_000, true)]
    [InlineData(-1, false)]
    [InlineData(600_001, false)]
    public void Validate_StopGraceMs_IsZeroOrCapped(int value, bool valid)
        => AssertValidity(WithProcess(c => c.StopGraceMs = value), valid, "StopGraceMs");

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(-1, false)]
    public void Validate_RealmId_IsNotNegative(int value, bool valid)
        => AssertValidity(WithProcess(c => c.RealmId = value), valid, "RealmId");

    [Fact]
    public void Validate_TwoProcessesWithTheSameRealmId_IsAProblem()
    {
        var options = new ClusterSupervisorOptions();
        options.Processes["a"] = new SupervisedProcessConfig { Name = "a", Role = SupervisedRoles.Combined, ExecutablePath = "x", RealmId = 2 };
        options.Processes["b"] = new SupervisedProcessConfig { Name = "b", Role = SupervisedRoles.Combined, ExecutablePath = "x", RealmId = 2 };

        string problem = Assert.Single(options.Validate());

        Assert.Contains("RealmId", problem, StringComparison.Ordinal);
        Assert.Contains("'a'", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(ClusterSupervisorOptions.CrashLoopMaxRestarts), 1, true)]
    [InlineData(nameof(ClusterSupervisorOptions.CrashLoopMaxRestarts), 1000, true)]
    [InlineData(nameof(ClusterSupervisorOptions.CrashLoopMaxRestarts), 0, false)]
    [InlineData(nameof(ClusterSupervisorOptions.CrashLoopMaxRestarts), 1001, false)]
    [InlineData(nameof(ClusterSupervisorOptions.CrashLoopWindowSeconds), 10, true)]
    [InlineData(nameof(ClusterSupervisorOptions.CrashLoopWindowSeconds), 86_400, true)]
    [InlineData(nameof(ClusterSupervisorOptions.CrashLoopWindowSeconds), 9, false)]
    [InlineData(nameof(ClusterSupervisorOptions.CrashLoopWindowSeconds), 86_401, false)]
    [InlineData(nameof(ClusterSupervisorOptions.HealthyUptimeSeconds), 1, true)]
    [InlineData(nameof(ClusterSupervisorOptions.HealthyUptimeSeconds), 86_400, true)]
    [InlineData(nameof(ClusterSupervisorOptions.HealthyUptimeSeconds), 0, false)]
    [InlineData(nameof(ClusterSupervisorOptions.HealthyUptimeSeconds), 86_401, false)]
    [InlineData(nameof(ClusterSupervisorOptions.RestartDelayMs), 0, true)]
    [InlineData(nameof(ClusterSupervisorOptions.RestartDelayMs), 600_000, true)]
    [InlineData(nameof(ClusterSupervisorOptions.RestartDelayMs), -1, false)]
    [InlineData(nameof(ClusterSupervisorOptions.RestartDelayMs), 600_001, false)]
    [InlineData(nameof(ClusterSupervisorOptions.HangCaptureTimeoutSeconds), 1, true)]
    [InlineData(nameof(ClusterSupervisorOptions.HangCaptureTimeoutSeconds), 3600, true)]
    [InlineData(nameof(ClusterSupervisorOptions.HangCaptureTimeoutSeconds), 0, false)]
    [InlineData(nameof(ClusterSupervisorOptions.HangCaptureTimeoutSeconds), 3601, false)]
    public void Validate_SupervisorKeys_AreRangeChecked(string key, int value, bool valid)
    {
        var options = new ClusterSupervisorOptions();
        typeof(ClusterSupervisorOptions).GetProperty(key)!.SetValue(options, value);

        AssertValidity(options, valid, key);
    }

    [Fact]
    public void Validate_StacksCapture_NeedsATool_AndAnyCaptureNeedsADirectory()
    {
        AssertValidity(WithProcess(c => c.HangCapture = SupervisedHangCaptureMode.Stacks), false, "StackCaptureTool");
        AssertValidity(WithProcess(c => c.HangCapture = SupervisedHangCaptureMode.Stacks, o => o.StackCaptureTool = "dotnet-stack"), true, "StackCaptureTool");
        AssertValidity(WithProcess(c => c.HangCapture = SupervisedHangCaptureMode.Dump, o => o.HangCaptureDirectory = " "), false, "HangCaptureDirectory");
        AssertValidity(WithProcess(c => c.HangCapture = SupervisedHangCaptureMode.None, o => o.HangCaptureDirectory = " "), true, "HangCaptureDirectory");
    }

    [Fact]
    public void Warnings_HangCaptureDump_NamesTheSecretsRisk()
    {
        ClusterSupervisorOptions dump = WithProcess(c => c.HangCapture = SupervisedHangCaptureMode.Dump, o => o.Enabled = true);
        ClusterSupervisorOptions none = WithProcess(c => c.HangCapture = SupervisedHangCaptureMode.None, o => o.Enabled = true);

        string warning = Assert.Single(dump.Warnings());

        Assert.Contains("HangCapture", warning, StringComparison.Ordinal);
        Assert.Contains("memory", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(none.Warnings());
    }

    [Fact]
    public void ConfigCheck_ReportsTheNewProblemsAndWarnings()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cluster:Supervisor:Enabled"] = "true",
            ["Cluster:Supervisor:MinClusterProcesses"] = "1",
            ["Cluster:Supervisor:Processes:world:Role"] = "combined",
            ["Cluster:Supervisor:Processes:world:ExecutablePath"] = "dotnet",
            ["Cluster:Supervisor:Processes:world:HangTimeoutSeconds"] = "5",
            ["Cluster:Supervisor:Processes:world:HangCapture"] = "Dump",
        }).Build();

        ConfigIssue[] issues = [.. new ClusterSupervisorConfigChecks().Check(configuration)];

        Assert.Contains(issues, i => i.Severity == ConfigSeverity.Error && i.Problem.Contains("HangTimeoutSeconds", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Severity == ConfigSeverity.Warning && i.Problem.Contains("HangCapture", StringComparison.Ordinal));
    }

    private static void AssertValidity(ClusterSupervisorOptions options, bool valid, string key)
    {
        string[] problems = [.. options.Validate()];
        if (valid)
        {
            Assert.Empty(problems);
        }
        else
        {
            Assert.Contains(problems, p => p.Contains(key, StringComparison.Ordinal));
        }
    }
}
